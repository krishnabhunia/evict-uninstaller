import Foundation

/// Well-known locations Evict reads from and writes to.
public enum AppPaths {
    public static let productName = "Evict"

    public static var home: String { NSHomeDirectory() }
    public static var userLibrary: String { home + "/Library" }
    public static let systemLibrary = "/Library"

    /// Where Evict keeps its own settings and history.
    public static var supportDirectory: String {
        let base = userLibrary + "/Application Support/" + productName
        try? FileManager.default.createDirectory(atPath: base, withIntermediateDirectories: true)
        return base
    }

    public static var historyFile: String { supportDirectory + "/history.json" }
    public static var settingsFile: String { supportDirectory + "/settings.json" }
    public static var logFile: String { supportDirectory + "/evict.log" }

    /// Folders scanned for installed applications.
    public static var applicationFolders: [String] {
        ["/Applications", home + "/Applications", "/Applications/Setapp"]
            .filter { FileManager.default.fileExists(atPath: $0) }
    }

    /// Homebrew's prefix on Apple Silicon and on Intel.
    public static var homebrewPrefixes: [String] {
        ["/opt/homebrew", "/usr/local"].filter { FileManager.default.fileExists(atPath: $0 + "/bin/brew") }
    }
}
