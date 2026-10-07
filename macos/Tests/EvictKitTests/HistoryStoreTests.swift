import XCTest
@testable import EvictKit

final class HistoryStoreTests: XCTestCase {
    private func withHistoryFile(_ test: (String) throws -> Void) throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("EvictHistoryTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try test(directory.appendingPathComponent("history.json").path)
    }

    private func entry() -> HistoryEntry {
        HistoryEntry(date: Date(timeIntervalSince1970: 1_700_000_000), appName: "Fixture App",
                     bundleIdentifier: "com.evict.fixture", itemsRemoved: 2, bytesFreed: 128, failures: 1)
    }

    func testSavedHistorySurvivesReloadAndNewStore() throws {
        try withHistoryFile { path in
            let expected = entry()
            let store = HistoryStore(path: path)
            store.append(expected)
            store.load()
            XCTAssertNil(store.lastError)
            XCTAssertEqual(store.entries.count, 1)
            XCTAssertEqual(store.entries.first?.date, expected.date)
            let reopened = HistoryStore(path: path)
            XCTAssertNil(reopened.lastError)
            XCTAssertEqual(reopened.entries.first?.id, expected.id)
        }
    }

    func testLegacyNumericDatesAreLoaded() throws {
        try withHistoryFile { path in
            let expected = entry()
            try JSONEncoder().encode([expected]).write(to: URL(fileURLWithPath: path))
            let store = HistoryStore(path: path)
            XCTAssertNil(store.lastError)
            XCTAssertEqual(store.entries.first?.date, expected.date)
        }
    }

    func testUnreadableHistoryIsPreservedUntilExplicitClear() throws {
        try withHistoryFile { path in
            let damaged = Data("unreadable history fixture".utf8)
            try damaged.write(to: URL(fileURLWithPath: path))
            let store = HistoryStore(path: path)
            XCTAssertNotNil(store.lastError)
            store.append(entry())
            store.load()
            XCTAssertEqual(store.entries.count, 1, "A failed reload must retain the in-memory entry")
            XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: path)), damaged)
            store.clear()
            XCTAssertNil(store.lastError)
            XCTAssertTrue(HistoryStore(path: path).entries.isEmpty)
            XCTAssertNotEqual(try Data(contentsOf: URL(fileURLWithPath: path)), damaged)
        }
    }

    func testConcurrentAppendsPersistEveryEntry() throws {
        try withHistoryFile { path in
            let store = HistoryStore(path: path)
            DispatchQueue.concurrentPerform(iterations: 20) { index in
                store.append(HistoryEntry(appName: "Fixture \(index)", bundleIdentifier: nil,
                                          itemsRemoved: 0, bytesFreed: 0, failures: 0))
            }
            XCTAssertEqual(store.entries.count, 20)
            XCTAssertEqual(HistoryStore(path: path).entries.count, 20)
        }
    }
}
