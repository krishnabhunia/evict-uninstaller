import Foundation

/// Reads launchd jobs (LaunchAgents / LaunchDaemons) – the Mac equivalent of the Windows Run keys
/// and Startup folder. Orphans (jobs whose program no longer exists) are flagged for cleanup.
public struct LaunchItems {

    public init() {}

    public static var folders: [(path: String, isDaemon: Bool, isSystem: Bool)] {
        [
            (AppPaths.userLibrary + "/LaunchAgents", false, false),
            ("/Library/LaunchAgents", false, true),
            ("/Library/LaunchDaemons", true, true),
        ]
    }

    public func scan() -> [StartupItem] {
        var items: [StartupItem] = []
        for folder in Self.folders {
            guard let entries = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { continue }
            for entry in entries.sorted() where entry.hasSuffix(".plist") {
                let path = folder.path + "/" + entry
                guard let item = describe(path: path, isDaemon: folder.isDaemon, isSystem: folder.isSystem) else { continue }
                items.append(item)
            }
        }
        return items.sorted { ($0.isOrphan ? 0 : 1, $0.label.lowercased()) < ($1.isOrphan ? 0 : 1, $1.label.lowercased()) }
    }

    public func describe(path: String, isDaemon: Bool, isSystem: Bool) -> StartupItem? {
        guard let plist = Plist.read(atPath: path) else {
            return StartupItem(path: path, label: (path as NSString).lastPathComponent, program: nil,
                               isDaemon: isDaemon, isSystemScope: isSystem, runsAtLoad: false, isOrphan: false)
        }
        let label = Plist.string(plist, "Label") ?? (path as NSString).lastPathComponent.replacingOccurrences(of: ".plist", with: "")
        let program = Plist.launchdProgram(plist)
        return StartupItem(path: path,
                           label: label,
                           program: program,
                           isDaemon: isDaemon,
                           isSystemScope: isSystem,
                           runsAtLoad: Plist.bool(plist, "RunAtLoad") || plist["StartInterval"] != nil || plist["StartCalendarInterval"] != nil,
                           isOrphan: Self.isOrphan(program: program))
    }

    /// A job is an orphan when the program it starts is gone. Arguments like `-c` are ignored;
    /// only an absolute path that no longer exists counts, so built-in jobs are never flagged.
    public static func isOrphan(program: String?) -> Bool {
        guard let program, program.hasPrefix("/") else { return false }
        if program.hasPrefix("/System/") || program.hasPrefix("/usr/bin/") || program.hasPrefix("/bin/") { return false }
        return !FileManager.default.fileExists(atPath: program)
    }

    /// Asks launchd to stop running the job before its plist is removed. Failures are ignored:
    /// a job that is not loaded is exactly the state we want anyway.
    public func unload(_ item: StartupItem) {
        guard !item.isSystemScope else { return }
        _ = Shell.run("/bin/launchctl", ["unload", "-w", item.path], timeout: 10)
    }
}
