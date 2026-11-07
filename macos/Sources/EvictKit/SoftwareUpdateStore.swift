import Foundation

/// Exclusions have no expiry. Update attempts retain their evidence for seven days, including
/// failures and interruptions. A damaged file is preserved and cannot silently drop exclusions.
public final class SoftwareUpdateStore: @unchecked Sendable {
    public static let retention: TimeInterval = 7 * 24 * 60 * 60
    private struct State: Codable {
        var schemaVersion = 1
        var exclusions: [SoftwareUpdateExclusion] = []
        var history: [SoftwareUpdateHistoryEntry] = []
    }

    private let path: String
    private let lock = NSLock()
    private var state = State()
    private var mayWrite = true
    private var error: String?

    public var exclusions: [SoftwareUpdateExclusion] {
        lock.lock(); defer { lock.unlock() }
        return state.exclusions.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    public var lastError: String? {
        lock.lock(); defer { lock.unlock() }
        return error
    }

    public var isReadable: Bool {
        lock.lock(); defer { lock.unlock() }
        return mayWrite
    }

    public init(path: String = AppPaths.supportDirectory + "/software-updates.json", now: Date = Date()) {
        self.path = path
        guard FileManager.default.fileExists(atPath: path) else { return }
        do {
            let data = try Data(contentsOf: URL(fileURLWithPath: path))
            let decoder = JSONDecoder()
            decoder.dateDecodingStrategy = .iso8601
            let decoded = try decoder.decode(State.self, from: data)
            guard decoded.schemaVersion == 1,
                  decoded.exclusions.allSatisfy({ HomebrewUpdateParser.isValidCaskID($0.caskID) }),
                  Set(decoded.exclusions.map(\.caskID)).count == decoded.exclusions.count,
                  Set(decoded.history.map(\.id)).count == decoded.history.count else {
                throw SoftwareUpdateError.unavailableStore("Unsupported or ambiguous software update data.")
            }
            state = decoded
            state.history = recent(state.history, now: now)
            for index in state.history.indices where state.history[index].status == .running {
                state.history[index].status = .interrupted
                state.history[index].details = "Evict closed before this update was verified. Check updates and inspect the installed app before retrying."
            }
            // Reconcile pending attempts immediately so reopening never reports an unverified success.
            if state.history != decoded.history { try save(state) }
        } catch {
            mayWrite = false
            self.error = "Saved software update settings could not be read or reconciled. The file has been preserved. Open it from Update History to repair it, or explicitly reset these records. \(error.localizedDescription)"
        }
    }

    public func history(now: Date = Date()) -> [SoftwareUpdateHistoryEntry] {
        lock.lock(); defer { lock.unlock() }
        let entries = recent(state.history, now: now)
        if entries != state.history {
            state.history = entries
            if mayWrite {
                do { try save(state) } catch { self.error = "Old update history could not be pruned: \(error.localizedDescription)" }
            }
        }
        return entries
    }

    public var fileURL: URL { URL(fileURLWithPath: path) }

    public func isExcluded(_ caskID: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return state.exclusions.contains { $0.caskID == caskID }
    }

    public func exclude(_ candidates: [SoftwareUpdateCandidate], now: Date = Date()) throws {
        lock.lock(); defer { lock.unlock() }
        try requireWritable()
        var next = state
        for candidate in candidates {
            guard HomebrewUpdateParser.isValidCaskID(candidate.caskID) else { throw SoftwareUpdateError.invalidCaskID }
            guard !next.exclusions.contains(where: { $0.caskID == candidate.caskID }) else { continue }
            next.exclusions.append(.init(caskID: candidate.caskID, name: candidate.name, addedAt: now))
        }
        try save(next)
        state = next
    }

    public func include(_ ids: Set<String>) throws {
        lock.lock(); defer { lock.unlock() }
        try requireWritable()
        var next = state
        next.exclusions.removeAll { ids.contains($0.caskID) }
        try save(next)
        state = next
    }

    /// Write the pending record before running an installer. If durable storage is unavailable,
    /// the caller must leave the package alone instead of losing the update's audit trail.
    public func begin(_ candidate: SoftwareUpdateCandidate, now: Date = Date()) throws -> SoftwareUpdateHistoryEntry {
        lock.lock(); defer { lock.unlock() }
        try requireWritable()
        guard !state.exclusions.contains(where: { $0.caskID == candidate.caskID }) else {
            throw SoftwareUpdateError.unavailableStore("This app was excluded. Restore it from Exclusions before updating.")
        }
        var next = state
        next.history = recent(next.history, now: now)
        let entry = SoftwareUpdateHistoryEntry(candidate: candidate, date: now)
        next.history.insert(entry, at: 0)
        try save(next)
        state = next
        return entry
    }

    public func finish(_ id: UUID, result: SoftwareUpdateResult, now: Date = Date()) throws {
        lock.lock(); defer { lock.unlock() }
        try requireWritable()
        guard let index = state.history.firstIndex(where: { $0.id == id }) else {
            throw SoftwareUpdateError.unavailableStore("The update attempt is missing from history.")
        }
        state.history[index].completedAt = now
        state.history[index].status = result.status
        state.history[index].recordedVersionAfter = result.recordedVersionAfter
        state.history[index].appVersionAfter = result.appVersionAfter
        state.history[index].exitStatus = result.exitStatus
        state.history[index].details = String(result.details.prefix(32_000))
        // Keep the outcome in memory if disk write fails. The persisted pending record will be
        // labelled interrupted on next launch, never as a successful update.
        try save(state)
    }

    public func clearHistory() throws {
        lock.lock(); defer { lock.unlock() }
        try requireWritable()
        guard !state.history.contains(where: { $0.status == .running }) else {
            throw SoftwareUpdateError.unavailableStore("An update is still running. Wait before clearing history.")
        }
        var next = state
        next.history = []
        try save(next)
        state = next
    }

    /// An explicit recovery action, exposed only after a user confirms that saved exclusions and
    /// history will be reset. Keep the damaged original as a timestamped backup for investigation.
    public func resetRecords(now: Date = Date()) throws {
        lock.lock(); defer { lock.unlock() }
        guard !state.history.contains(where: { $0.status == .running }) else {
            throw SoftwareUpdateError.unavailableStore("Wait for the running update before resetting records.")
        }
        let file = URL(fileURLWithPath: path)
        if FileManager.default.fileExists(atPath: path) {
            let backup = file.deletingPathExtension().appendingPathExtension("backup-\(Int(now.timeIntervalSince1970))-\(UUID().uuidString).json")
            try FileManager.default.copyItem(at: file, to: backup)
        }
        try save(State())
        state = State()
        mayWrite = true
    }

    private func recent(_ entries: [SoftwareUpdateHistoryEntry], now: Date) -> [SoftwareUpdateHistoryEntry] {
        let cutoff = now.addingTimeInterval(-Self.retention)
        // Preserve a running operation even if the machine's clock jumps while it is updating.
        return entries.filter { $0.date >= cutoff || $0.status == .running }.sorted { $0.date > $1.date }
    }

    private func requireWritable() throws {
        guard mayWrite else {
            throw SoftwareUpdateError.unavailableStore(error ?? "Saved software update records are unavailable.")
        }
    }

    /// Always called under the lock, except during construction before the store is shared.
    private func save(_ snapshot: State) throws {
        do {
            let file = URL(fileURLWithPath: path)
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .iso8601
            try encoder.encode(snapshot).write(to: file, options: .atomic)
            error = nil
        } catch {
            self.error = "Software update records could not be saved: \(error.localizedDescription)"
            throw SoftwareUpdateError.unavailableStore(self.error!)
        }
    }
}
