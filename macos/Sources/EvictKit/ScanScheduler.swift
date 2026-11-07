import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

public struct ScanSchedule: Codable, Sendable, Equatable {
    public enum Frequency: String, Codable, CaseIterable, Sendable {
        case never = "Never", daily = "Every day", weekly = "Once a week"
    }
    public var frequency: Frequency
    public var hour: Int
    public var minute: Int
    /// launchd uses 0 for Sunday, 1 for Monday, through 6 for Saturday.
    public var weekday: Int
    public var token: String

    public init(frequency: Frequency = .never, hour: Int = 10, minute: Int = 0, weekday: Int = 1, token: String = "") {
        self.frequency = frequency
        self.hour = hour
        self.minute = minute
        self.weekday = weekday
        self.token = token
    }

    public var isValid: Bool { (0...23).contains(hour) && (0...59).contains(minute) && (0...6).contains(weekday) }
    public var calendar: [String: Int] {
        var value = ["Hour": hour, "Minute": minute]
        if frequency == .weekly { value["Weekday"] = weekday }
        return value
    }
}

public enum ScanScheduleError: LocalizedError {
    case invalid(String), registration(String)
    public var errorDescription: String? {
        switch self {
        case .invalid(let text), .registration(let text): return text
        }
    }
}

/// Only Evict's own per-user agent is changed. Applying a schedule is a transaction:
/// register, verify, then persist preferences; restore the previous agent on any failure.
public struct ScanScheduler {
    public static let label = "app.evict.mac.scheduled-scan"
    public static var defaultPath: String { AppPaths.userLibrary + "/LaunchAgents/" + label + ".plist" }
    public typealias Runner = (String, [String]) -> Shell.Output
    private let plistPath: String
    private let domain: String
    private let run: Runner
    private let applicationRoots: [String]

    public init(plistPath: String = ScanScheduler.defaultPath, userID: UInt32 = getuid(),
                applicationRoots: [String] = ["/Applications", NSHomeDirectory() + "/Applications"],
                runner: @escaping Runner = { Shell.run($0, $1, timeout: 10) }) {
        self.plistPath = plistPath
        domain = "gui/\(userID)"
        run = runner
        self.applicationRoots = applicationRoots
    }

    public static func propertyList(schedule: ScanSchedule, executable: String) throws -> Data {
        guard schedule.isValid, schedule.frequency != .never, UUID(uuidString: schedule.token) != nil,
              executable.hasPrefix("/"), !executable.contains("\n"), !executable.contains("\0") else {
            throw ScanScheduleError.invalid("The scan schedule or application path is invalid.")
        }
        let value: [String: Any] = [
            "Label": label,
            "ProgramArguments": [executable, "--scheduled-scan", "--schedule-token", schedule.token],
            "StartCalendarInterval": schedule.calendar,
            "RunAtLoad": false,
            "ProcessType": "Background",
            "LowPriorityIO": true,
            "Nice": 10,
        ]
        return try PropertyListSerialization.data(fromPropertyList: value, format: .xml, options: 0)
    }

    public static func isOwned(_ data: Data) -> Bool {
        guard let p = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              p["Label"] as? String == label,
              let args = p["ProgramArguments"] as? [String], args.count == 4,
              args[0].hasSuffix(".app/Contents/MacOS/Evict"),
              args[1] == "--scheduled-scan", args[2] == "--schedule-token",
              UUID(uuidString: args[3]) != nil else { return false }
        return true
    }

    private func isLoaded(_ target: String) throws -> Bool {
        let output = run("/bin/launchctl", ["print", target])
        if output.ok { return true }
        let message = (output.stderr + output.stdout).lowercased()
        if [Int32(3), Int32(113)].contains(output.status) &&
            (message.contains("could not find service") || message.contains("service not found")) { return false }
        throw ScanScheduleError.registration("Could not verify the scan agent: \(output.stderr.isEmpty ? "launchctl exited \(output.status)" : output.stderr)")
    }

    @discardableResult
    public func apply(_ requested: ScanSchedule, executable: String,
                      persist: (ScanSchedule) throws -> Void) throws -> ScanSchedule {
        guard requested.isValid else { throw ScanScheduleError.invalid("Choose a valid day and time.") }
        let fm = FileManager.default
        let url = URL(fileURLWithPath: plistPath)
        if (try? fm.attributesOfItem(atPath: plistPath)[.type]) as? FileAttributeType == .typeSymbolicLink {
            throw ScanScheduleError.invalid("Evict's schedule path is a symbolic link; it was not changed.")
        }
        let parent = url.deletingLastPathComponent()
        guard parent.standardizedFileURL.path == parent.resolvingSymlinksInPath().path else {
            throw ScanScheduleError.invalid("The LaunchAgents folder resolves through a symbolic link.")
        }
        let previous = fm.contents(atPath: plistPath)
        if previous == nil && fm.fileExists(atPath: plistPath) {
            throw ScanScheduleError.invalid("The existing schedule file is unreadable; it was not changed.")
        }
        if let previous, !Self.isOwned(previous) {
            throw ScanScheduleError.invalid("The existing job is not an Evict scan schedule; it was not changed.")
        }
        let target = domain + "/" + Self.label
        let wasLoaded = try isLoaded(target)
        if wasLoaded && previous == nil {
            throw ScanScheduleError.invalid("An unrecognized job already uses Evict's schedule label.")
        }
        var schedule = requested
        if schedule.frequency != .never {
            let executableURL = URL(fileURLWithPath: executable)
            guard executable.hasSuffix(".app/Contents/MacOS/Evict"),
                  applicationRoots.contains(where: { executable.hasPrefix($0 + "/") }),
                  !executable.hasPrefix("/Volumes/"), !executable.contains("/AppTranslocation/"),
                  executableURL.standardizedFileURL.path == executableURL.resolvingSymlinksInPath().path,
                  fm.isExecutableFile(atPath: executable) else {
                throw ScanScheduleError.invalid("Install Evict in Applications before enabling a scheduled scan.")
            }
            schedule.token = UUID().uuidString
        } else { schedule.token = "" }
        var changed = false
        do {
            if wasLoaded {
                let stopped = run("/bin/launchctl", ["bootout", target])
                guard stopped.ok else { throw ScanScheduleError.registration(stopped.stderr) }
                changed = true
            }
            if schedule.frequency == .never {
                if previous != nil { try fm.removeItem(at: url); changed = true }
                guard try !isLoaded(target) else {
                    throw ScanScheduleError.registration("The old scan agent is still loaded.")
                }
            } else {
                try fm.createDirectory(at: parent, withIntermediateDirectories: true)
                try Self.propertyList(schedule: schedule, executable: executable).write(to: url, options: .atomic)
                changed = true
                try fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: plistPath)
                let loaded = run("/bin/launchctl", ["bootstrap", domain, plistPath])
                guard loaded.ok else { throw ScanScheduleError.registration(loaded.stderr) }
                let proof = run("/bin/launchctl", ["print", target])
                guard proof.ok, proof.stdout.contains(executable), proof.stdout.contains(schedule.token),
                      fm.contents(atPath: plistPath) == (try Self.propertyList(schedule: schedule, executable: executable)) else {
                    throw ScanScheduleError.registration("launchd did not confirm the requested scan schedule.")
                }
            }
            try persist(schedule)
            return schedule
        } catch {
            var restoreError = ""
            if changed {
                do {
                    if try isLoaded(target) {
                        let output = run("/bin/launchctl", ["bootout", target])
                        if !output.ok { throw ScanScheduleError.registration(output.stderr) }
                    }
                    if let previous {
                        try previous.write(to: url, options: .atomic)
                        if wasLoaded {
                            let output = run("/bin/launchctl", ["bootstrap", domain, plistPath])
                            if !output.ok { throw ScanScheduleError.registration(output.stderr) }
                        }
                    } else if fm.fileExists(atPath: plistPath) { try fm.removeItem(at: url) }
                    guard try isLoaded(target) == wasLoaded else {
                        throw ScanScheduleError.registration("The previous registration could not be verified.")
                    }
                } catch { restoreError = error.localizedDescription }
            }
            let detail = restoreError.isEmpty ? "Previous schedule retained." : "Previous schedule could not be restored: \(restoreError)"
            throw ScanScheduleError.registration("\(error.localizedDescription) \(detail)")
        }
    }
}

public struct ScheduledScanReport: Codable, Sendable {
    public let date: Date
    public let applicationCount: Int
    public let applicationBytes: Int64
    public let orphanStartupItems: Int
    public static var path: String { AppPaths.supportDirectory + "/scheduled-scan.json" }
    public static func load() -> ScheduledScanReport? {
        guard let data = FileManager.default.contents(atPath: path) else { return nil }
        return try? JSONDecoder().decode(Self.self, from: data)
    }
}

public enum ScheduledScanRunner {
    /// No background work starts without the saved enabled schedule AND matching owned plist.
    public static func authorized(arguments: [String], settings: Settings, plist: Data?) -> Bool {
        guard arguments.count == 4, arguments[1] == "--scheduled-scan", arguments[2] == "--schedule-token",
              settings.scanSchedule.frequency != .never, settings.scanSchedule.isValid,
              arguments[3] == settings.scanSchedule.token, let plist, ScanScheduler.isOwned(plist),
              let p = try? PropertyListSerialization.propertyList(from: plist, format: nil) as? [String: Any],
              p["ProgramArguments"] as? [String] == arguments,
              p["StartCalendarInterval"] as? [String: Int] == settings.scanSchedule.calendar else { return false }
        return true
    }

    public static func run(arguments: [String] = CommandLine.arguments) -> Int32 {
        let settings = SettingsStore().current
        guard authorized(arguments: arguments, settings: settings,
                         plist: FileManager.default.contents(atPath: ScanScheduler.defaultPath)) else { return 2 }
        let lock = open(AppPaths.supportDirectory + "/scheduled-scan.lock", O_CREAT | O_RDWR | O_NOFOLLOW, 0o600)
        guard lock >= 0 else { return 3 }
        defer { close(lock) }
        guard flock(lock, LOCK_EX | LOCK_NB) == 0 else { return 0 }
        defer { _ = flock(lock, LOCK_UN) }
        let currentSettings = SettingsStore().current
        guard authorized(arguments: arguments, settings: currentSettings,
                         plist: FileManager.default.contents(atPath: ScanScheduler.defaultPath)) else { return 2 }
        let apps = AppInventory().scan(includeSystemApps: currentSettings.includeSystemApps)
        let report = ScheduledScanReport(date: Date(), applicationCount: apps.count,
                                         applicationBytes: apps.reduce(0) { $0 + $1.sizeBytes },
                                         orphanStartupItems: LaunchItems().scan().filter(\.isOrphan).count)
        do {
            let data = try JSONEncoder().encode(report)
            try data.write(to: URL(fileURLWithPath: ScheduledScanReport.path), options: .atomic)
            return 0
        } catch { return 4 }
    }
}
