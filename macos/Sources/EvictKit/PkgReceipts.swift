import Foundation

/// Reads the installer receipt database (`pkgutil`). Receipts are how Evict knows which files
/// an `.pkg` installer put on disk – the closest macOS gets to a Windows uninstall entry.
public struct PkgReceipts {
    public struct Receipt: Sendable {
        public let identifier: String
        public let version: String?
        public let installLocation: String?
        public let installedAt: Date?
    }

    private static let pkgutil = "/usr/sbin/pkgutil"
    private var cache: [String]?

    public init() {}

    /// All receipt identifiers on this Mac.
    public static func allIdentifiers() -> [String] {
        let output = Shell.run(pkgutil, ["--pkgs"], timeout: 30)
        return output.ok ? output.lines : []
    }

    public static func info(_ identifier: String) -> Receipt? {
        let output = Shell.run(pkgutil, ["--pkg-info", identifier])
        guard output.ok else { return nil }
        var version: String?, location: String?, time: Date?
        for line in output.lines {
            let parts = line.split(separator: ":", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 2 else { continue }
            switch parts[0] {
            case "version": version = parts[1]
            case "location": location = parts[1].hasPrefix("/") ? parts[1] : "/" + parts[1]
            case "install-time": if let seconds = TimeInterval(parts[1]) { time = Date(timeIntervalSince1970: seconds) }
            default: break
            }
        }
        return Receipt(identifier: identifier, version: version, installLocation: location, installedAt: time)
    }

    /// Receipt identifiers that belong to an application, matched on its bundle identifier or name.
    public static func identifiers(forApp app: InstalledApp, allIdentifiers: [String]) -> [String] {
        allIdentifiers.filter { identifier in
            if let bundleID = app.bundleIdentifier, NameMatching.matchesBundleIdentifier(identifier, bundleIdentifier: bundleID) { return true }
            if let prefix = app.bundleIdentifierPrefix, NameMatching.matchesVendorPrefix(identifier, bundleIdentifierPrefix: prefix),
               NameMatching.matchesAppName(identifier, appName: app.name) { return true }
            return false
        }
    }

    /// Absolute paths a receipt installed. Used by Force Uninstall to find files outside /Applications.
    public static func files(_ identifier: String) -> [String] {
        guard let receipt = info(identifier) else { return [] }
        let base = receipt.installLocation ?? "/"
        let output = Shell.run(pkgutil, ["--only-files", "--files", identifier], timeout: 30)
        guard output.ok else { return [] }
        return output.lines.map { line in
            let path = base.hasSuffix("/") ? base + line : base + "/" + line
            return SafePaths.normalize(path)
        }
    }
}
