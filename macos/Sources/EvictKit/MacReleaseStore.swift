import Foundation

public struct MacReleasePreferences: Codable, Equatable, Sendable {
    public var includeBeta = false
    public var showAllBuilds = false
    public var pinnedVersion: String?
    public var skippedTags: [String] = []

    public init() {}

    private enum CodingKeys: String, CodingKey { case includeBeta, showAllBuilds, pinnedVersion, skippedTags }
    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        includeBeta = try values.decodeIfPresent(Bool.self, forKey: .includeBeta) ?? false
        showAllBuilds = try values.decodeIfPresent(Bool.self, forKey: .showAllBuilds) ?? false
        pinnedVersion = try values.decodeIfPresent(String.self, forKey: .pinnedVersion)
        skippedTags = try values.decodeIfPresent([String].self, forKey: .skippedTags) ?? []
    }
}

public enum MacReleaseOperation: String, Codable, Sendable { case install, rollback }
public enum MacReleaseStatus: String, Codable, Sendable {
    case prepared, handedOff, verified, failed, cancelled

    public var title: String {
        switch self {
        case .prepared: return "Ready to install"
        case .handedOff: return "Awaiting installation"
        case .verified: return "Installed and verified"
        case .failed: return "Failed"
        case .cancelled: return "Cancelled"
        }
    }
}

public struct MacReleaseHistoryEntry: Codable, Identifiable, Equatable, Sendable {
    public let id: UUID
    public let operation: MacReleaseOperation
    public let fromVersion: String
    public let toVersion: String
    public let releaseTag: String?
    public let createdAt: Date
    public var completedAt: Date?
    public var status: MacReleaseStatus
    public var detail: String
    public let installationPath: String
    public let executableDigest: String

    public init(id: UUID = UUID(), operation: MacReleaseOperation, fromVersion: String, toVersion: String,
                releaseTag: String?, createdAt: Date = Date(), status: MacReleaseStatus = .prepared,
                detail: String, installationPath: String, executableDigest: String) {
        self.id = id; self.operation = operation; self.fromVersion = fromVersion; self.toVersion = toVersion
        self.releaseTag = releaseTag; self.createdAt = createdAt; self.status = status; self.detail = detail
        self.installationPath = installationPath; self.executableDigest = executableDigest
    }
}

public struct MacReleaseRecoveryCopy: Codable, Identifiable, Equatable, Sendable {
    public let id: UUID
    public let version: String
    public let createdAt: Date
    public let archiveDigest: String
    public let archiveBytes: Int64
    public let originalInstallationPath: String

    public init(id: UUID, version: String, createdAt: Date = Date(), archiveDigest: String,
                archiveBytes: Int64, originalInstallationPath: String) {
        self.id = id; self.version = version; self.createdAt = createdAt; self.archiveDigest = archiveDigest
        self.archiveBytes = archiveBytes; self.originalInstallationPath = originalInstallationPath
    }
}

public struct MacReleaseSavedState: Codable, Equatable, Sendable {
    public var preferences = MacReleasePreferences()
    public var history: [MacReleaseHistoryEntry] = []
    public var recoveryCopies: [MacReleaseRecoveryCopy] = []

    public init() {}
    private enum CodingKeys: String, CodingKey { case preferences, history, recoveryCopies }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        preferences = try values.decodeIfPresent(MacReleasePreferences.self, forKey: .preferences) ?? MacReleasePreferences()
        history = try values.decodeIfPresent([MacReleaseHistoryEntry].self, forKey: .history) ?? []
        recoveryCopies = try values.decodeIfPresent([MacReleaseRecoveryCopy].self, forKey: .recoveryCopies) ?? []
    }
}

/// All state transitions are atomic and serialized. A corrupt file is preserved and surfaced,
/// not silently replaced with defaults that would lose the user's pin or recovery history.
public final class MacReleaseStore: @unchecked Sendable {
    public let directory: URL
    private let lock = NSLock()
    private var saved = MacReleaseSavedState()
    private var failure: String?
    private var writable = true

    public init(directory: URL = URL(fileURLWithPath: AppPaths.supportDirectory).appendingPathComponent("Version Manager", isDirectory: true)) {
        self.directory = directory.standardizedFileURL
        let path = directory.appendingPathComponent("state.json")
        guard FileManager.default.fileExists(atPath: path.path) else { return }
        do {
            let attributes = try FileManager.default.attributesOfItem(atPath: path.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular else {
                throw MacReleaseError.file("The version history is not a regular file.")
            }
            let data = try Data(contentsOf: path)
            guard data.count <= 4 * 1024 * 1024 else { throw MacReleaseError.file("The version history is unexpectedly large.") }
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            saved = try decoder.decode(MacReleaseSavedState.self, from: data)
        } catch {
            failure = "Version preferences and recovery history could not be read. The file was preserved: \(error.localizedDescription)"
            writable = false
        }
    }

    public var snapshot: MacReleaseSavedState {
        lock.lock(); defer { lock.unlock() }
        return saved
    }

    public var lastError: String? {
        lock.lock(); defer { lock.unlock() }
        return failure
    }

    public func update(_ change: (inout MacReleaseSavedState) -> Void) throws {
        lock.lock(); defer { lock.unlock() }
        guard writable else { throw MacReleaseError.file(failure ?? "Version preferences are unavailable.") }
        var candidate = saved
        change(&candidate)
        if candidate.history.count > 500 { candidate.history = Array(candidate.history.prefix(500)) }
        candidate.preferences.skippedTags = Array(Set(candidate.preferences.skippedTags)).sorted()
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let attributes = try FileManager.default.attributesOfItem(atPath: directory.path)
            guard attributes[.type] as? FileAttributeType == .typeDirectory else {
                throw MacReleaseError.file("The version manager folder is not a regular directory.")
            }
            let target = directory.appendingPathComponent("state.json")
            if FileManager.default.fileExists(atPath: target.path) {
                let attributes = try FileManager.default.attributesOfItem(atPath: target.path)
                guard attributes[.type] as? FileAttributeType == .typeRegular else {
                    throw MacReleaseError.file("The version history file has been replaced by a link or special file.")
                }
            }
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .iso8601
            try encoder.encode(candidate).write(to: target, options: .atomic)
            saved = candidate
            failure = nil
        } catch {
            failure = "Version preferences could not be saved: \(error.localizedDescription)"
            throw MacReleaseError.file(failure!)
        }
    }

    public func stagingDirectory(for id: UUID) -> URL {
        directory.appendingPathComponent("Staging", isDirectory: true).appendingPathComponent(id.uuidString, isDirectory: true)
    }

    public func recoveryArchive(for id: UUID) -> URL {
        directory.appendingPathComponent("Recovery", isDirectory: true).appendingPathComponent(id.uuidString + ".zip")
    }

    /// The running app must be the exact staged executable at the original installation path.
    /// Merely launching a downloaded copy from staging does not count as a completed update.
    public func reconcile(version: String, bundlePath: String, executableDigest: String, now: Date = Date()) throws -> Bool {
        let candidates = snapshot.history.filter {
            [.prepared, .handedOff].contains($0.status) && $0.toVersion == version &&
                $0.installationPath == bundlePath && $0.executableDigest == executableDigest
        }
        guard !candidates.isEmpty else { return false }
        let ids = Set(candidates.map(\.id))
        try update { state in
            for index in state.history.indices where ids.contains(state.history[index].id) {
                state.history[index].status = .verified
                state.history[index].completedAt = now
                state.history[index].detail = "Launched the requested version at the original installation path; its executable SHA-256 matches the verified staged copy."
            }
            if state.preferences.pinnedVersion != nil { state.preferences.pinnedVersion = version }
        }
        return true
    }
}
