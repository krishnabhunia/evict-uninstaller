import Foundation

/// The History page's backing file – a plain JSON list in Evict's support folder.
public final class HistoryStore: @unchecked Sendable {
    private let lock = NSLock()
    private(set) public var entries: [HistoryEntry] = []
    private let path: String

    public init(path: String = AppPaths.historyFile) {
        self.path = path
        load()
    }

    public func load() {
        lock.lock(); defer { lock.unlock() }
        guard let data = FileManager.default.contents(atPath: path) else { entries = []; return }
        entries = (try? JSONDecoder().decode([HistoryEntry].self, from: data)) ?? []
    }

    public func append(_ entry: HistoryEntry) {
        lock.lock()
        entries.insert(entry, at: 0)
        if entries.count > 500 { entries = Array(entries.prefix(500)) }
        let snapshot = entries
        lock.unlock()
        save(snapshot)
    }

    public func clear() {
        lock.lock(); entries = []; lock.unlock()
        save([])
    }

    private func save(_ snapshot: [HistoryEntry]) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted]
        encoder.dateEncodingStrategy = .iso8601
        guard let data = try? encoder.encode(snapshot) else { return }
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}
