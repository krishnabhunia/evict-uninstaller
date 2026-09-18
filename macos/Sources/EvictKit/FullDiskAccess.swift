import Foundation

/// macOS hides other apps' data behind Full Disk Access (TCC). Without it Evict can still remove an
/// app, but several leftover folders are invisible – so the app checks and explains rather than
/// silently finding nothing.
public enum FullDiskAccess {

    /// Probes a folder only readable with Full Disk Access granted.
    public static var isGranted: Bool {
        let probes = [
            AppPaths.userLibrary + "/Application Support/com.apple.TCC",
            AppPaths.userLibrary + "/Safari",
        ]
        for probe in probes where FileManager.default.fileExists(atPath: probe) {
            if (try? FileManager.default.contentsOfDirectory(atPath: probe)) != nil { return true }
        }
        // Neither probe exists (unusual) – assume granted rather than nagging the user forever.
        return !probes.contains { FileManager.default.fileExists(atPath: $0) }
    }

    public static let settingsURL = "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles"

    /// Opens System Settings at Privacy & Security → Full Disk Access.
    public static func openSettings() {
        #if os(macOS)
        _ = Shell.run("/usr/bin/open", [settingsURL], timeout: 5)
        #endif
    }

    public static let explanation = """
    Evict needs Full Disk Access to see the support files, caches and preferences other apps keep in \
    your Library folder. Without it, an uninstall still removes the app itself, but some leftovers stay hidden.
    """
}
