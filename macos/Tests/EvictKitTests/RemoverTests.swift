import XCTest
@testable import EvictKit

final class RemoverTests: XCTestCase {
    /// Each test owns a uniquely named fixture under an allowed cache root. Its fake
    /// mover only relocates that fixture; it never calls the real macOS Trash API.
    private func withFixtures(_ test: (URL) throws -> Void) throws {
        let directory = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Caches/EvictRemoverTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try test(directory)
    }

    private func item(_ path: String) -> LeftoverItem {
        LeftoverItem(path: path, kind: .caches, confidence: .high, sizeBytes: 0, reason: "Isolated test fixture")
    }

    func testDanglingLinkIsMovedAndDoesNotCountItsTargetsBytes() throws {
        try withFixtures { directory in
            let link = directory.appendingPathComponent("shortcut")
            let moved = directory.appendingPathComponent("moved-shortcut")
            try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: directory.appendingPathComponent("missing.app/tool").path)
            XCTAssertFalse(FileManager.default.fileExists(atPath: link.path))
            XCTAssertTrue(Remover.entryExists(link.path))
            let remover = Remover(moveToTrash: { path in
                try FileManager.default.moveItem(atPath: path, toPath: moved.path)
            })
            let result = remover.remove([item(link.path)])
            XCTAssertEqual(result.succeededCount, 1)
            XCTAssertTrue(result.failed.isEmpty)
            XCTAssertEqual(result.bytesFreed, 0)
            XCTAssertFalse(Remover.entryExists(link.path))
            XCTAssertTrue(Remover.entryExists(moved.path))
        }
    }

    func testLiveSymlinkDoesNotWalkOrRemoveDestinationData() throws {
        try withFixtures { directory in
            let target = directory.appendingPathComponent("bundle-data")
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
            try Data(repeating: 1, count: 8_192).write(to: target.appendingPathComponent("payload"))
            let link = directory.appendingPathComponent("shortcut")
            let moved = directory.appendingPathComponent("moved-shortcut")
            try FileManager.default.createSymbolicLink(atPath: link.path, withDestinationPath: target.path)
            let remover = Remover(moveToTrash: { path in
                try FileManager.default.moveItem(atPath: path, toPath: moved.path)
            })
            let result = remover.remove([item(link.path)])
            XCTAssertEqual(result.succeededCount, 1)
            XCTAssertEqual(result.bytesFreed, 0)
            XCTAssertTrue(FileManager.default.fileExists(atPath: target.appendingPathComponent("payload").path))
        }
    }

    func testPartialFailureCountsOnlyTheVerifiedMove() throws {
        try withFixtures { directory in
            let success = directory.appendingPathComponent("success")
            let failure = directory.appendingPathComponent("failure")
            let moved = directory.appendingPathComponent("moved-success")
            try Data([1]).write(to: success)
            try Data([2]).write(to: failure)
            let remover = Remover(moveToTrash: { path in
                if path == failure.path { throw Remover.RemovalError.system("Fixture failure") }
                try FileManager.default.moveItem(atPath: path, toPath: moved.path)
            })
            let result = remover.remove([item(success.path), item(failure.path)])
            XCTAssertEqual(result.succeededCount, 1)
            XCTAssertEqual(result.failed.count, 1)
            XCTAssertEqual(result.stillPresent, [failure.path])
        }
    }

    func testMissingOrUnmovedItemsAreNotReportedAsSuccesses() throws {
        try withFixtures { directory in
            let existing = directory.appendingPathComponent("still-present")
            try Data([1]).write(to: existing)
            let missing = directory.appendingPathComponent("missing")
            let remover = Remover(moveToTrash: { _ in /* Intentionally does not move the fixture. */ })
            let result = remover.remove([item(existing.path), item(missing.path)])
            XCTAssertEqual(result.succeededCount, 0)
            XCTAssertEqual(result.failed.count, 2)
            XCTAssertEqual(result.stillPresent, [existing.path])
            XCTAssertTrue(result.trashed.isEmpty)
        }
    }
}
