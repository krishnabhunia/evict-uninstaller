import Foundation

public struct MacInstallFileStamp: Codable, Sendable, Equatable {
    public let path: String
    public let modified: Date?
    public let bytes: Int64
    public let bundleVersion: String?
    public let isDirectory: Bool

    public init(path: String, modified: Date?, bytes: Int64, bundleVersion: String? = nil, isDirectory: Bool) {
        self.path = path
        self.modified = modified
        self.bytes = bytes
        self.bundleVersion = bundleVersion
        self.isDirectory = isDirectory
    }
}

public struct MacInstallSnapshot: Codable, Sendable {
    public let date: Date
    public let entries: [MacInstallFileStamp]
    public let incomplete: Bool
    public let warnings: [String]

    public init(date: Date = Date(), entries: [MacInstallFileStamp], incomplete: Bool = false, warnings: [String] = []) {
        self.date = date
        self.entries = entries
        self.incomplete = incomplete
        self.warnings = warnings
    }
}

public struct MacInstallChange: Identifiable, Codable, Equatable, Sendable {
    public enum Kind: String, Codable, Sendable { case added = "Added", changed = "Changed", removed = "Removed" }
    public var id: String { path }
    public let kind: Kind
    public let path: String
    public let beforeVersion: String?
    public let afterVersion: String?
}

public struct MacInstallSession: Identifiable, Codable, Sendable {
    public let id: UUID
    public let started: Date
    public let finished: Date
    public let changes: [MacInstallChange]
    public let incomplete: Bool
    public let warnings: [String]

    public var addedApps: [MacInstallChange] {
        changes.filter { $0.kind == .added && ($0.path as NSString).pathExtension.lowercased() == "app" }
    }
}

/// A bounded, explicit before/after monitor. Differences are observations, not proof that a
/// particular installer owns the paths. Nothing in a recording is ever removed automatically.
public enum MacInstallMonitor {
    public static var roots: [String] {
        ["/Applications", AppPaths.home + "/Applications", AppPaths.userLibrary + "/Application Support",
         AppPaths.userLibrary + "/Preferences", AppPaths.userLibrary + "/LaunchAgents",
         "/Library/Application Support", "/Library/LaunchAgents", "/Library/LaunchDaemons"]
    }

    public static func capture() -> MacInstallSnapshot { capture(roots: roots) }

    static func capture(roots: [String], maximumEntries: Int = 12_000) -> MacInstallSnapshot {
        let fm = FileManager.default
        var entries: [MacInstallFileStamp] = []
        var warnings: [String] = []
        var seen = Set<String>()
        var incomplete = false
        let ownData = AppPaths.supportDirectory
        for root in roots {
            guard fm.fileExists(atPath: root) else { continue }
            guard MacMaintenancePaths.isUnaliased(root) else {
                incomplete = true
                warnings.append("Linked folder skipped: \(root)")
                continue
            }
            var rootUnreadable = false
            guard let walker = fm.enumerator(at: URL(fileURLWithPath: root), includingPropertiesForKeys: nil,
                                             options: [.skipsHiddenFiles, .skipsPackageDescendants],
                                             errorHandler: { _, _ in rootUnreadable = true; return true }) else {
                incomplete = true
                warnings.append("Could not read \(root)")
                continue
            }
            for case let url as URL in walker {
                if entries.count >= maximumEntries { incomplete = true; break }
                // Foundation may canonicalize /var and /private/var differently for
                // returned URLs. Traversal depth must not depend on path-string lengths.
                let depth = walker.level
                if depth >= 2 { walker.skipDescendants() }
                if depth > 2 { continue }
                guard seen.insert(url.path).inserted,
                      let info = try? fm.attributesOfItem(atPath: url.path) else { continue }
                let type = info[.type] as? FileAttributeType
                if type == .typeSymbolicLink { walker.skipDescendants(); continue }
                guard type == .typeDirectory || type == .typeRegular else { continue }
                if SafePaths.isInside(url.path, root: ownData) { walker.skipDescendants(); continue }
                var bundleVersion: String?
                if url.pathExtension.lowercased() == "app" {
                    walker.skipDescendants()
                    let plist = Plist.read(atPath: url.path + "/Contents/Info.plist") ?? [:]
                    bundleVersion = Plist.string(plist, "CFBundleShortVersionString") ?? Plist.string(plist, "CFBundleVersion")
                }
                entries.append(MacInstallFileStamp(path: url.path, modified: info[.modificationDate] as? Date,
                    bytes: type == .typeRegular ? (info[.size] as? NSNumber)?.int64Value ?? 0 : 0,
                    bundleVersion: bundleVersion, isDirectory: type == .typeDirectory))
            }
            if rootUnreadable { incomplete = true; warnings.append("Some folders under \(root) could not be read.") }
            if entries.count >= maximumEntries {
                warnings.append("The recording reached its \(maximumEntries)-entry limit.")
                break
            }
        }
        return MacInstallSnapshot(entries: entries.sorted { $0.path < $1.path }, incomplete: incomplete, warnings: warnings)
    }

    public static func compare(before: MacInstallSnapshot, after: MacInstallSnapshot) -> MacInstallSession {
        // Last observation wins for duplicate fixture/legacy paths; never crash on a malformed journal.
        let old = Dictionary(before.entries.map { ($0.path, $0) }, uniquingKeysWith: { _, new in new })
        let new = Dictionary(after.entries.map { ($0.path, $0) }, uniquingKeysWith: { _, new in new })
        var changes: [MacInstallChange] = []
        for path in Set(old.keys).union(new.keys).sorted() {
            let previous = old[path], current = new[path]
            // An incomplete scan cannot prove absence. Suppress added/removed in that direction.
            if previous == nil && !before.incomplete {
                changes.append(MacInstallChange(kind: .added, path: path, beforeVersion: nil, afterVersion: current?.bundleVersion))
            } else if current == nil && !after.incomplete {
                changes.append(MacInstallChange(kind: .removed, path: path, beforeVersion: previous?.bundleVersion, afterVersion: nil))
            } else if let previous, let current, previous != current {
                changes.append(MacInstallChange(kind: .changed, path: path, beforeVersion: previous.bundleVersion, afterVersion: current.bundleVersion))
            }
        }
        return MacInstallSession(id: UUID(), started: before.date, finished: after.date, changes: changes,
                                 incomplete: before.incomplete || after.incomplete,
                                 warnings: Array(Set(before.warnings + after.warnings)).sorted())
    }
}

public final class MacInstallMonitorStore: @unchecked Sendable {
    private struct Payload: Codable {
        var schema = 1
        var recording: MacInstallSnapshot?
        var sessions: [MacInstallSession] = []
    }

    public enum StoreError: LocalizedError {
        case unreadable, alreadyRecording, noRecording, unsupported
        public var errorDescription: String? {
            switch self {
            case .unreadable: return "The install recording could not be read. Its file has been preserved."
            case .alreadyRecording: return "A recording is already in progress. Finish it before starting another."
            case .noRecording: return "There is no recording to finish."
            case .unsupported: return "This recording was created by a newer version. Its file has been preserved."
            }
        }
    }

    private let lock = NSLock()
    private let url: URL
    private var payload = Payload()
    private var loadError: Error?

    public init(path: String = AppPaths.supportDirectory + "/install-monitor.json") {
        url = URL(fileURLWithPath: path)
        guard FileManager.default.fileExists(atPath: path) else { return }
        do {
            let data = try Data(contentsOf: url)
            let decoder = JSONDecoder()
            // Numeric timestamps preserve filesystem subsecond precision across a restart.
            decoder.dateDecodingStrategy = .secondsSince1970
            let legacy = JSONDecoder()
            legacy.dateDecodingStrategy = .iso8601
            let decoded = try (try? decoder.decode(Payload.self, from: data)) ?? legacy.decode(Payload.self, from: data)
            guard decoded.schema == 1 else { throw StoreError.unsupported }
            payload = decoded
        } catch { loadError = error }
    }

    public var recording: MacInstallSnapshot? { lock.lock(); defer { lock.unlock() }; return payload.recording }
    public var sessions: [MacInstallSession] { lock.lock(); defer { lock.unlock() }; return payload.sessions }
    public var lastError: String? { lock.lock(); defer { lock.unlock() }; return loadError?.localizedDescription }

    public func begin(_ snapshot: MacInstallSnapshot) throws {
        lock.lock(); defer { lock.unlock() }
        guard loadError == nil else { throw StoreError.unreadable }
        guard payload.recording == nil else { throw StoreError.alreadyRecording }
        var updated = payload
        updated.recording = snapshot
        try save(updated)
        payload = updated
    }

    @discardableResult public func finish(_ snapshot: MacInstallSnapshot) throws -> MacInstallSession {
        lock.lock(); defer { lock.unlock() }
        guard loadError == nil else { throw StoreError.unreadable }
        guard let before = payload.recording else { throw StoreError.noRecording }
        let session = MacInstallMonitor.compare(before: before, after: snapshot)
        var updated = payload
        updated.recording = nil
        updated.sessions = Array(([session] + updated.sessions).prefix(25))
        try save(updated)
        payload = updated
        return session
    }

    public func cancelRecording() throws {
        lock.lock(); defer { lock.unlock() }
        guard loadError == nil else { throw StoreError.unreadable }
        var updated = payload
        updated.recording = nil
        try save(updated)
        payload = updated
    }

    private func save(_ snapshot: Payload) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .secondsSince1970
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(snapshot)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}
