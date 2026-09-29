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

    /// Ticked by default: the app itself and anything Evict is confident about.
    /// Low-confidence finds are listed but never pre-selected.
    public var defaultSelection: Set<String> {
        Set(allItems.filter { $0.confidence >= .medium && !$0.needsAdmin }.map(\.id))
    }
}

/// Builds and carries out an uninstall.
public struct UninstallPlanner {
    private let scanner = LeftoverScanner()
    private let remover = Remover()

    public init() {}

    public func plan(for app: InstalledApp, casks: [String] = []) -> UninstallPlan {
        let bundleItem = app.source.isRemovable
            ? LeftoverItem(path: app.bundlePath, kind: .appBundle, confidence: .high, sizeBytes: app.sizeBytes,
                           reason: "The application itself", needsAdmin: SafePaths.needsAdmin(app.bundlePath))
            : nil
        let leftovers = scanner.scan(LeftoverScanner.Target(app: app))
        return UninstallPlan(app: app,
                             bundleItem: bundleItem,
                             leftovers: leftovers,
                             homebrewCask: HomebrewService.cask(forBundlePath: app.bundlePath, casks: casks))
    }

    /// Force Uninstall: the app bundle may be gone already, so the search is driven by a name
    /// (and, when a bundle is still there, by its identifier).
    public func plan(forLeftoverName name: String, bundlePath: String?) -> UninstallPlan {
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
            guard FileManager.default.fileExists(atPath: path) else { return nil }
            return LeftoverItem(path: path, kind: .appBundle, confidence: .high, sizeBytes: FileSize.measure(path),
                                reason: "The application itself", needsAdmin: SafePaths.needsAdmin(path))
        }
        return UninstallPlan(app: app, bundleItem: bundleItem, leftovers: scanner.scan(target), homebrewCask: nil)
    }

    /// Carries out the removal for the ticked items and writes a history entry.
    @discardableResult
    public func execute(plan: UninstallPlan, selection: Set<String>, history: HistoryStore? = nil,
                        progress: ((Double, String) -> Void)? = nil) -> RemovalResult {
        let items = plan.allItems.filter { selection.contains($0.id) }
        // Unload launchd jobs before their plists disappear.
        let launchItems = LaunchItems()
        for item in items where item.kind == .launchItem {
            launchItems.unload(StartupItem(path: item.path, label: item.displayName, program: nil,
                                           isDaemon: false, isSystemScope: item.needsAdmin, runsAtLoad: false, isOrphan: false))
        }
        let result = remover.remove(items, progress: progress)
        history?.append(HistoryEntry(appName: plan.app.name,
                                     bundleIdentifier: plan.app.bundleIdentifier,
                                     itemsRemoved: result.succeededCount,
                                     bytesFreed: result.bytesFreed,
                                     failures: result.failed.count))
        Log.info("Uninstalled \(plan.app.name): \(result.succeededCount)/\(items.count) items, \(ByteFormat.string(result.bytesFreed)) freed, \(result.failed.count) failures")
        return result
    }
}
