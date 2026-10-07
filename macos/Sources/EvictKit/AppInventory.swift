import Foundation

/// Builds the list of installed applications – the Mac equivalent of the Windows "Programs" page.
///
/// macOS has no single registry of installed software, so four sources are combined:
/// application bundles on disk, installer receipts (`pkgutil`), Homebrew casks and App Store receipts.
public struct AppInventory {

    public init() {}

    /// Scans every application folder. `progress` is called with a 0–1 fraction.
    public func scan(includeSystemApps: Bool = false, progress: ((Double, String) -> Void)? = nil) -> [InstalledApp] {
        let bundles = bundlePaths(includeSystemApps: includeSystemApps)
        let receipts = PkgReceipts.allIdentifiers()
        let casks = HomebrewService.installedCasks()
        var apps: [InstalledApp] = []
        apps.reserveCapacity(bundles.count)

        for (index, path) in bundles.enumerated() {
            progress?(Double(index) / Double(max(bundles.count, 1)), (path as NSString).lastPathComponent)
            if let app = describe(bundlePath: path, receipts: receipts, casks: casks) {
                apps.append(app)
            }
        }
        progress?(1, "")
        return apps.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Every `.app` bundle in the application folders, one level of grouping folders included
    /// (`/Applications/Vendor/App.app`).
    public func bundlePaths(includeSystemApps: Bool, folders: [String]? = nil) -> [String] {
        let fm = FileManager.default
        var found: [String] = []
        var scanFolders = folders ?? AppPaths.applicationFolders
        if includeSystemApps { scanFolders.append("/System/Applications") }

        for folder in scanFolders {
            guard let entries = try? fm.contentsOfDirectory(atPath: folder) else { continue }
            for entry in entries.sorted() {
                let path = folder + "/" + entry
                if entry.hasSuffix(".app") {
                    found.append(path)
                } else if !entry.hasPrefix(".") {
                    var isDirectory: ObjCBool = false
                    guard fm.fileExists(atPath: path, isDirectory: &isDirectory), isDirectory.boolValue else { continue }
                    guard let children = try? fm.contentsOfDirectory(atPath: path) else { continue }
                    found.append(contentsOf: children.filter { $0.hasSuffix(".app") }.sorted().map { path + "/" + $0 })
                }
            }
        }
        var seen = Set<String>()
        return found.compactMap { path in
            let normalized = URL(fileURLWithPath: path).standardizedFileURL.path
            return seen.insert(normalized).inserted ? normalized : nil
        }
    }

    /// Validate a dropped application's structure before treating it as a removal target.
    public static func validBundlePath(_ path: String) -> String? {
        let normalized = URL(fileURLWithPath: path).standardizedFileURL.path
        var isDirectory: ObjCBool = false
        guard (normalized as NSString).pathExtension.lowercased() == "app",
              FileManager.default.fileExists(atPath: normalized, isDirectory: &isDirectory), isDirectory.boolValue,
              Plist.read(atPath: normalized + "/Contents/Info.plist") != nil else { return nil }
        return normalized
    }

    /// Reads one bundle's `Info.plist` and works out where it came from.
    public func describe(bundlePath: String, receipts: [String], casks: [String]) -> InstalledApp? {
        let infoPath = bundlePath + "/Contents/Info.plist"
        let info = Plist.read(atPath: infoPath) ?? [:]
        let fallbackName = (bundlePath as NSString).lastPathComponent.replacingOccurrences(of: ".app", with: "")
        let name = Plist.string(info, "CFBundleDisplayName") ?? Plist.string(info, "CFBundleName") ?? fallbackName
        let bundleID = Plist.string(info, "CFBundleIdentifier")
        let version = Plist.string(info, "CFBundleShortVersionString") ?? Plist.string(info, "CFBundleVersion")

        let source = self.source(bundlePath: bundlePath, bundleID: bundleID, receipts: receipts, casks: casks)
        let partial = InstalledApp(bundlePath: bundlePath, name: name, bundleIdentifier: bundleID, version: version,
                                   source: source, sizeBytes: 0)
        let matchedReceipts = PkgReceipts.identifiers(forApp: partial, allIdentifiers: receipts)

        return InstalledApp(
            bundlePath: bundlePath,
            name: name,
            bundleIdentifier: bundleID,
            version: version,
            vendorName: bundleID.flatMap { NameMatching.vendorToken(fromBundleIdentifier: $0) },
            source: source,
            sizeBytes: FileSize.measure(bundlePath),
            installedDate: FileSize.created(bundlePath),
            lastUsedDate: lastUsed(bundlePath),
            receiptIdentifiers: matchedReceipts)
    }

    func source(bundlePath: String, bundleID: String?, receipts: [String], casks: [String]) -> AppSource {
        let original = URL(fileURLWithPath: bundlePath).standardizedFileURL.path
        let resolved = URL(fileURLWithPath: bundlePath).resolvingSymlinksInPath().standardizedFileURL.path
        // Preserve protected locations even when the bundle is a link. The writable Data
        // volume is not a system application source merely because its path starts /System.
        let protectedOriginal = SafePaths.systemRoots.contains { SafePaths.isInside(original, root: $0) }
        let protectedResolved = !SafePaths.isInside(resolved, root: "/System/Volumes/Data") &&
            SafePaths.systemRoots.contains { SafePaths.isInside(resolved, root: $0) }
        if protectedOriginal || protectedResolved { return .system }
        if FileManager.default.fileExists(atPath: bundlePath + "/Contents/_MASReceipt/receipt") { return .appStore }
        if HomebrewService.cask(forBundlePath: bundlePath, casks: casks) != nil { return .homebrewCask }
        if let id = bundleID, receipts.contains(where: { NameMatching.matchesBundleIdentifier($0, bundleIdentifier: id) }) { return .installerPkg }
        return .dragInstalled
    }

    /// Last time the app was opened, as macOS records it on the bundle.
    private func lastUsed(_ bundlePath: String) -> Date? {
        #if os(macOS)
        let url = URL(fileURLWithPath: bundlePath)
        if let values = try? url.resourceValues(forKeys: [.contentAccessDateKey]), let date = values.contentAccessDate {
            return date
        }
        #endif
        return FileSize.modified(bundlePath)
    }
}
