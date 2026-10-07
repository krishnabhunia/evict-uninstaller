import Foundation

/// One group of leftovers in the review step ("Preferences – 3 items, 12 KB").
public struct LeftoverGroup: Identifiable, Sendable {
    public var id: String { kind.rawValue }
    public let kind: LeftoverKind
    public let items: [LeftoverItem]
    public var totalBytes: Int64 { items.reduce(0) { $0 + $1.sizeBytes } }
    public var needsAdmin: Bool { items.contains { $0.needsAdmin } }
}

/// Everything Evict proposes to remove for one application.
public struct UninstallPlan: Sendable {
    public let app: InstalledApp
    public let bundleItem: LeftoverItem?
    public let leftovers: [LeftoverItem]
    public let homebrewCask: String?

    public var allItems: [LeftoverItem] { (bundleItem.map { [$0] } ?? []) + leftovers }

    public var groups: [LeftoverGroup] {
        let order: [LeftoverKind] = [.appBundle, .supportFiles, .containers, .preferences, .caches, .savedState,
                                     .logs, .launchItem, .privilegedHelper, .plugin, .binary, .receipt]
        return order.compactMap { kind in
            let items = allItems.filter { $0.kind == kind }
            return items.isEmpty ? nil : LeftoverGroup(kind: kind, items: items)
        }
    }

    public var totalBytes: Int64 { allItems.reduce(0) { $0 + $1.sizeBytes } }

    public var defaultSelection: Set<String> { initialSelection(settings: Settings()) }

    /// Select the application explicitly, even when authorization may be needed.
    /// Shared vendor data always requires a manual choice, including when low-confidence
    /// preselection is enabled. Authorization errors are reported after the actual attempt.
    public func initialSelection(settings: Settings) -> Set<String> {
        Set(allItems.filter { item in
            guard item.isSharedVendorMatch != true else { return false }
            if item.kind == .appBundle { return SafePaths.check(item.path).isAllowed }
            guard !item.needsAdmin, item.receiptIdentifier == nil else { return false }
            return item.confidence >= .medium ||
                (settings.showLowConfidenceItems && settings.preselectLowConfidence)
        }.map(\.id))
    }

    /// Remove shortcuts before their destination bundle, so they do not become dangling.
    func removalItems(selection: Set<String>) -> [LeftoverItem] {
        let selected = allItems.filter { selection.contains($0.id) }
        return selected.filter { $0.kind == .binary } + selected.filter { $0.kind != .binary }
    }
}

/// Builds and carries out an uninstall.
public struct UninstallPlanner: Sendable {
    private let scanner = LeftoverScanner()
    private let remover = Remover()

    public init() {}

    public func plan(for app: InstalledApp, casks: [String] = [], settings: Settings = Settings()) -> UninstallPlan {
        let bundleItem = app.source.isRemovable && SafePaths.check(app.bundlePath).isAllowed
            ? LeftoverItem(path: app.bundlePath, kind: .appBundle, confidence: .high, sizeBytes: app.sizeBytes,
                           reason: "The application itself", needsAdmin: SafePaths.needsAdmin(app.bundlePath))
            : nil
        let leftovers = scanner.scan(LeftoverScanner.Target(app: app), settings: settings)
        return UninstallPlan(app: app,
                             bundleItem: bundleItem,
                             leftovers: leftovers,
                             homebrewCask: HomebrewService.cask(forBundlePath: app.bundlePath, casks: casks))
    }

    /// Force Uninstall: the app bundle may be gone already, so the search is driven by a name
    /// (and, when a bundle is still there, by its identifier).
    public func plan(forLeftoverName name: String, bundlePath: String?, settings: Settings = Settings()) -> UninstallPlan {
        // A dropped file or ordinary directory must not become a whole-directory removal.
        let bundlePath = bundlePath.flatMap { AppInventory.validBundlePath($0) }
        var bundleIdentifier: String?
        if let bundlePath, let info = Plist.read(atPath: bundlePath + "/Contents/Info.plist") {
            bundleIdentifier = Plist.string(info, "CFBundleIdentifier")
        }
        let prefix = bundleIdentifier.flatMap { id -> String? in
            let parts = id.split(separator: ".")
            return parts.count >= 2 ? parts.prefix(2).joined(separator: ".") : nil
        }
        let app = InstalledApp(bundlePath: bundlePath ?? "", name: name, bundleIdentifier: bundleIdentifier,
                               version: nil, source: .unknown, sizeBytes: bundlePath.map { FileSize.measure($0) } ?? 0)
        let target = LeftoverScanner.Target(name: name, bundleIdentifier: bundleIdentifier,
                                            bundleIdentifierPrefix: prefix, bundlePath: bundlePath)
        let bundleItem = bundlePath.flatMap { path -> LeftoverItem? in
            guard SafePaths.check(path).isAllowed,
                  let described = AppInventory().describe(bundlePath: path, receipts: [], casks: []),
                  described.source.isRemovable else { return nil }
            return LeftoverItem(path: path, kind: .appBundle, confidence: .high, sizeBytes: FileSize.measure(path),
                                reason: "The application itself", needsAdmin: SafePaths.needsAdmin(path))
        }
        return UninstallPlan(app: app, bundleItem: bundleItem, leftovers: scanner.scan(target, settings: settings), homebrewCask: nil)
    }

    /// Carries out the removal for the ticked items and writes a history entry.
    @discardableResult
    public func execute(plan: UninstallPlan, selection: Set<String>, history: HistoryStore? = nil,
                        progress: ((Double, String) -> Void)? = nil) -> RemovalResult {
        let items = plan.removalItems(selection: selection)
        // Unload launchd jobs before their plists disappear.
        let launchItems = LaunchItems()
        for item in items where item.kind == .launchItem {
            launchItems.unload(StartupItem(path: item.path, label: item.displayName, program: nil,
                                           isDaemon: SafePaths.isInside(item.path, root: "/Library/LaunchDaemons"),
                                           isSystemScope: SafePaths.isInside(item.path, root: AppPaths.systemLibrary),
                                           runsAtLoad: false, isOrphan: false))
        }
        let result = remover.remove(items, progress: progress)
        history?.append(HistoryEntry(appName: plan.app.name,
                                     bundleIdentifier: plan.app.bundleIdentifier,
                                     itemsRemoved: result.succeededCount,
                                     bytesFreed: result.bytesFreed,
                                     failures: result.failed.count))
        Log.info("Removal for \(plan.app.name): \(result.succeededCount)/\(items.count) items moved to Trash, \(ByteFormat.string(result.bytesFreed)) moved, \(result.failed.count) failures")
        return result
    }
}
