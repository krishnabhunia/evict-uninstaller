import Foundation

/// Finds the files an application leaves behind – the Mac counterpart of the Windows registry sweep.
///
/// Only the folders in `SearchRoot.all` are looked at, only their direct children are considered,
/// and every candidate must both match the application *and* pass `SafePaths.check` before it is
/// ever shown to the user.
public struct LeftoverScanner {

    public struct SearchRoot: Sendable {
        public let path: String
        public let kind: LeftoverKind
        /// Match only entries whose name ends with this, e.g. `.plist` for Preferences.
        public let suffix: String?

        public init(_ path: String, _ kind: LeftoverKind, suffix: String? = nil) {
            self.path = path
            self.kind = kind
            self.suffix = suffix
        }

        public var needsAdmin: Bool { SafePaths.needsAdmin(path) }
    }

    /// What the app being removed looks like, so the same scan can serve Force Uninstall
    /// (where there may be no bundle left at all).
    public struct Target: Sendable {
        public let name: String
        public let bundleIdentifier: String?
        public let bundleIdentifierPrefix: String?
        public let bundlePath: String?
        public let receiptIdentifiers: [String]

        public init(name: String, bundleIdentifier: String?, bundleIdentifierPrefix: String?, bundlePath: String?, receiptIdentifiers: [String] = []) {
            self.name = name
            self.bundleIdentifier = bundleIdentifier
            self.bundleIdentifierPrefix = bundleIdentifierPrefix
            self.bundlePath = bundlePath
            self.receiptIdentifiers = receiptIdentifiers
        }

        public init(app: InstalledApp) {
            self.init(name: app.name,
                      bundleIdentifier: app.bundleIdentifier,
                      bundleIdentifierPrefix: app.bundleIdentifierPrefix,
                      bundlePath: app.bundlePath,
                      receiptIdentifiers: app.receiptIdentifiers)
        }
    }

    public init() {}

    public static var userRoots: [SearchRoot] {
        let library = AppPaths.userLibrary
        return [
            SearchRoot(library + "/Application Support", .supportFiles),
            SearchRoot(library + "/Caches", .caches),
            SearchRoot(library + "/Preferences", .preferences, suffix: ".plist"),
            SearchRoot(library + "/Preferences/ByHost", .preferences, suffix: ".plist"),
            SearchRoot(library + "/Containers", .containers),
            SearchRoot(library + "/Group Containers", .containers),
            SearchRoot(library + "/Saved Application State", .savedState),
            SearchRoot(library + "/Autosave Information", .savedState),
            SearchRoot(library + "/Logs", .logs),
            SearchRoot(library + "/HTTPStorages", .supportFiles),
            SearchRoot(library + "/WebKit", .supportFiles),
            SearchRoot(library + "/Cookies", .supportFiles),
            SearchRoot(library + "/Application Scripts", .supportFiles),
            SearchRoot(library + "/LaunchAgents", .launchItem, suffix: ".plist"),
            SearchRoot(library + "/Internet Plug-Ins", .plugin),
            SearchRoot(library + "/PreferencePanes", .plugin),
            SearchRoot(library + "/Services", .plugin),
        ]
    }

    public static var systemRoots: [SearchRoot] {
        let library = AppPaths.systemLibrary
        return [
            SearchRoot(library + "/Application Support", .supportFiles),
            SearchRoot(library + "/Caches", .caches),
            SearchRoot(library + "/Preferences", .preferences, suffix: ".plist"),
            SearchRoot(library + "/Logs", .logs),
            SearchRoot(library + "/LaunchAgents", .launchItem, suffix: ".plist"),
            SearchRoot(library + "/LaunchDaemons", .launchItem, suffix: ".plist"),
            SearchRoot(library + "/PrivilegedHelperTools", .privilegedHelper),
            SearchRoot(library + "/Internet Plug-Ins", .plugin),
            SearchRoot(library + "/PreferencePanes", .plugin),
            SearchRoot(library + "/QuickLook", .plugin),
            SearchRoot(library + "/Spotlight", .plugin),
            SearchRoot(library + "/ScriptingAdditions", .plugin),
            SearchRoot(library + "/Audio/Plug-Ins/Components", .plugin),
            SearchRoot(library + "/Audio/Plug-Ins/HAL", .plugin),
        ]
    }

    public static var allRoots: [SearchRoot] { userRoots + systemRoots }

    /// Everything that looks like it belongs to `target`, sorted strongest evidence first.
    public func scan(_ target: Target, roots: [SearchRoot] = LeftoverScanner.allRoots) -> [LeftoverItem] {
        var items: [LeftoverItem] = []
        var seen = Set<String>()

        for root in roots {
            guard let entries = try? FileManager.default.contentsOfDirectory(atPath: root.path) else { continue }
            for entry in entries where !entry.hasPrefix(".") {
                if let suffix = root.suffix, !entry.hasSuffix(suffix) { continue }
                let path = root.path + "/" + entry
                guard !seen.contains(path) else { continue }
                guard let match = evaluate(entry: entry, path: path, kind: root.kind, target: target) else { continue }
                guard SafePaths.check(path).isAllowed else { continue }
                seen.insert(path)
                items.append(match)
            }
        }

        items.append(contentsOf: commandLineLinks(target, seen: &seen))
        items.append(contentsOf: target.receiptIdentifiers.map {
            LeftoverItem(path: "pkgutil:" + $0, kind: .receipt, confidence: .high, sizeBytes: 0,
                         reason: "Installer receipt for this app", receiptIdentifier: $0, needsAdmin: true)
        })

        return items.sorted {
            $0.confidence != $1.confidence ? $0.confidence > $1.confidence : $0.sizeBytes > $1.sizeBytes
        }
    }

    /// The matching rules, in order of strength. Returns nil when nothing links the entry to the app.
    func evaluate(entry: String, path: String, kind: LeftoverKind, target: Target) -> LeftoverItem? {
        let bare = strippedName(entry)
        let needsAdmin = SafePaths.needsAdmin(path)

        func item(_ confidence: Confidence, _ reason: String) -> LeftoverItem {
            LeftoverItem(path: path, kind: kind, confidence: confidence,
                         sizeBytes: FileSize.measure(path), reason: reason, needsAdmin: needsAdmin)
        }

        // 1. A launchd job whose program lives inside the app bundle – the strongest possible link.
        if kind == .launchItem, let bundlePath = target.bundlePath,
           let plist = Plist.read(atPath: path), let program = Plist.launchdProgram(plist),
           SafePaths.isInside(program, root: bundlePath) {
            return item(.high, "Starts \(program) from inside the app")
        }

        // 2. Named after the bundle identifier.
        if let bundleID = target.bundleIdentifier, NameMatching.matchesBundleIdentifier(bare, bundleIdentifier: bundleID) {
            return item(.high, "Named after the app's bundle ID (\(bundleID))")
        }

        // 3. Same vendor prefix – likely a helper of the same app, but could be a sibling product.
        if let prefix = target.bundleIdentifierPrefix, NameMatching.matchesVendorPrefix(bare, bundleIdentifierPrefix: prefix) {
            let strong = NameMatching.matchesAppName(bare, appName: target.name)
            return item(strong ? .high : .medium,
                        strong ? "Vendor folder that also names the app" : "Belongs to the same vendor (\(prefix))")
        }

        // 4. Named after the app itself.
        if NameMatching.matchesAppName(bare, appName: target.name) {
            let exact = NameMatching.normalize(bare) == NameMatching.normalize(target.name)
            return item(exact ? .medium : .low,
                        exact ? "Folder named exactly like the app" : "Name looks like the app's")
        }

        return nil
    }

    /// Symlinks in `/usr/local/bin` that point into the app bundle (command-line helpers).
    private func commandLineLinks(_ target: Target, seen: inout Set<String>) -> [LeftoverItem] {
        guard let bundlePath = target.bundlePath else { return [] }
        let fm = FileManager.default
        var items: [LeftoverItem] = []
        for folder in ["/usr/local/bin", "/opt/homebrew/bin"] {
            guard let entries = try? fm.contentsOfDirectory(atPath: folder) else { continue }
            for entry in entries {
                let path = folder + "/" + entry
                guard !seen.contains(path),
                      let destination = try? fm.destinationOfSymbolicLink(atPath: path) else { continue }
                let resolved = destination.hasPrefix("/") ? destination : folder + "/" + destination
                guard SafePaths.isInside(SafePaths.normalize(resolved), root: bundlePath),
                      SafePaths.check(path).isAllowed else { continue }
                seen.insert(path)
                items.append(LeftoverItem(path: path, kind: .binary, confidence: .high, sizeBytes: 0,
                                          reason: "Command-line shortcut pointing into the app",
                                          needsAdmin: SafePaths.needsAdmin(path)))
            }
        }
        return items
    }

    /// "com.vendor.app.plist" → "com.vendor.app", "Vendor App.savedState" → "Vendor App".
    func strippedName(_ entry: String) -> String {
        var name = entry
        for suffix in [".plist", ".savedState", ".binarycookies", ".app", ".bundle", ".prefPane", ".qlgenerator",
                       ".mdimporter", ".component", ".plugin", ".saver", ".driver", ".osax"] where name.hasSuffix(suffix) {
            name = String(name.dropLast(suffix.count))
        }
        // Per-host preferences carry a hardware UUID: "com.vendor.app.ABCD-1234"
        if let range = name.range(of: #"\.[0-9A-F]{8}-([0-9A-F]{4}-){3}[0-9A-F]{12}$"#, options: [.regularExpression, .caseInsensitive]) {
            name = String(name[name.startIndex..<range.lowerBound])
        }
        return name
    }
}
