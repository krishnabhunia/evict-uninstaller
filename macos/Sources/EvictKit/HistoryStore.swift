import Foundation

/// The History page's backing file – a plain JSON list in Evict's support folder.
public final class HistoryStore: @unchecked Sendable {
    private let lock = NSLock()
    private var storedEntries: [HistoryEntry] = []
    private var storedError: String?
    private var canSave = true
    private let path: String

    public var entries: [HistoryEntry] {
        lock.lock(); defer { lock.unlock() }
        return storedEntries
    }

    public var lastError: String? {
        lock.lock(); defer { lock.unlock() }
        return storedError
    }

    public init(path: String = AppPaths.historyFile) {
        self.path = path
        load()
    }

    public func load() {
        lock.lock(); defer { lock.unlock() }
        guard FileManager.default.fileExists(atPath: path) else {
            storedEntries = []; storedError = nil; canSave = true
            return
        }
        guard let data = FileManager.default.contents(atPath: path) else {
            storedError = "History could not be read. Its file has been preserved."
            canSave = false
            return
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        // Also accept older numeric Date encodings without discarding those records.
        if let decoded = (try? decoder.decode([HistoryEntry].self, from: data)) ??
            (try? JSONDecoder().decode([HistoryEntry].self, from: data)) {
            storedEntries = decoded; storedError = nil; canSave = true
        } else {
            storedError = "History could not be decoded. Its file has been preserved; new entries are kept in memory until it is repaired or explicitly cleared."
            canSave = false
        }
    }

    public func append(_ entry: HistoryEntry) {
        lock.lock(); defer { lock.unlock() }
        storedEntries.insert(entry, at: 0)
        if storedEntries.count > 500 { storedEntries = Array(storedEntries.prefix(500)) }
        saveLocked(storedEntries)
    }

    public func clear() {
        lock.lock(); defer { lock.unlock() }
        storedEntries = []; canSave = true
        saveLocked([])
    }

    /// Called with the lock held, so concurrent updates cannot save an older snapshot last.
    private func saveLocked(_ snapshot: [HistoryEntry]) {
        guard canSave else { return }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted]
        encoder.dateEncodingStrategy = .iso8601
        do {
            let data = try encoder.encode(snapshot)
            try data.write(to: URL(fileURLWithPath: path), options: .atomic)
            storedError = nil
        } catch {
            storedError = "History could not be saved: \(error.localizedDescription)"
        }
    }
}
