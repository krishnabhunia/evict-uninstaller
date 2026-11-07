import XCTest
@testable import EvictKit

final class SoftwareUpdateStoreTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    private func withFile(_ test: (String) throws -> Void) throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("EvictSoftwareUpdates-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try test(directory.appendingPathComponent("updates.json").path)
    }

    func testSavedExclusionSurvivesRestartAndPreventsAnyPendingInstallRecord() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            try store.exclude([SoftwareUpdateFixtures.candidate], now: now)
            let reopened = SoftwareUpdateStore(path: path, now: now)
            XCTAssertTrue(reopened.isExcluded(SoftwareUpdateFixtures.candidate.id))
            XCTAssertThrowsError(try reopened.begin(SoftwareUpdateFixtures.candidate, now: now))
            XCTAssertTrue(reopened.history(now: now).isEmpty)
            try reopened.include([SoftwareUpdateFixtures.candidate.id])
            XCTAssertFalse(SoftwareUpdateStore(path: path, now: now).isExcluded(SoftwareUpdateFixtures.candidate.id))
        }
    }

    func testVerifiedHistoryKeepsActualVersionEvidenceAcrossRestart() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            let pending = try store.begin(SoftwareUpdateFixtures.candidate, now: now)
            try store.finish(pending.id, result: .init(status: .updated, recordedVersionAfter: "2.0", appVersionAfter: "2.0", exitStatus: 0, details: "Both sources verified"), now: now.addingTimeInterval(30))
            let saved = try XCTUnwrap(SoftwareUpdateStore(path: path, now: now).history(now: now).first)
            XCTAssertEqual(saved.id, pending.id)
            XCTAssertEqual(saved.status, .updated)
            XCTAssertEqual(saved.installedVersionBefore, "1.0")
            XCTAssertEqual(saved.installedVersionAfter, "2.0")
            XCTAssertEqual(saved.details, "Both sources verified")
        }
    }

    func testInterruptedUpdateDoesNotBecomeVerifiedOnNextLaunch() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            _ = try store.begin(SoftwareUpdateFixtures.candidate, now: now)
            let reopened = SoftwareUpdateStore(path: path, now: now)
            XCTAssertEqual(reopened.history(now: now).first?.status, .interrupted)
            XCTAssertNil(reopened.history(now: now).first?.installedVersionAfter)
            XCTAssertEqual(SoftwareUpdateStore(path: path, now: now).history(now: now).first?.status, .interrupted)
        }
    }

    func testSevenDayRetentionDoesNotExpireExclusions() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            let boundary = now.addingTimeInterval(-SoftwareUpdateStore.retention)
            let expired = try store.begin(SoftwareUpdateFixtures.candidate, now: boundary.addingTimeInterval(-1))
            try store.finish(expired.id, result: .init(status: .failed, details: "Expired fixture"), now: boundary)
            let keep = try store.begin(SoftwareUpdateFixtures.candidate, now: boundary)
            try store.finish(keep.id, result: .init(status: .failed, details: "Boundary fixture"), now: boundary)
            try store.exclude([SoftwareUpdateFixtures.candidate], now: boundary)
            let reopened = SoftwareUpdateStore(path: path, now: now)
            XCTAssertEqual(reopened.history(now: now).map(\.id), [keep.id])
            XCTAssertTrue(reopened.isExcluded(SoftwareUpdateFixtures.candidate.id))
            XCTAssertTrue(reopened.history(now: now.addingTimeInterval(1)).isEmpty)
        }
    }

    func testDamagedFileIsPreservedAndCannotSilentlyLoseExclusions() throws {
        try withFile { path in
            let damaged = Data("{ damaged software update settings".utf8)
            try damaged.write(to: URL(fileURLWithPath: path))
            let store = SoftwareUpdateStore(path: path, now: now)
            XCTAssertFalse(store.isReadable)
            XCTAssertNotNil(store.lastError)
            XCTAssertThrowsError(try store.exclude([SoftwareUpdateFixtures.candidate], now: now))
            XCTAssertThrowsError(try store.begin(SoftwareUpdateFixtures.candidate, now: now))
            XCTAssertEqual(try Data(contentsOf: URL(fileURLWithPath: path)), damaged)
            try store.resetRecords(now: now)
            XCTAssertTrue(store.isReadable)
            XCTAssertNil(store.lastError)
            let backups = try FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: path).deletingLastPathComponent(), includingPropertiesForKeys: nil)
                .filter { $0.lastPathComponent.contains(".backup-") }
            XCTAssertEqual(backups.count, 1)
            XCTAssertEqual(try Data(contentsOf: XCTUnwrap(backups.first)), damaged)
        }
    }

    func testStorageFailurePreventsPendingInstallFromBeingAccepted() throws {
        try withFile { path in
            try Data("Parent is a file".utf8).write(to: URL(fileURLWithPath: path))
            let store = SoftwareUpdateStore(path: path + "/child.json", now: now)
            XCTAssertThrowsError(try store.begin(SoftwareUpdateFixtures.candidate, now: now))
            XCTAssertTrue(store.history(now: now).isEmpty)
            XCTAssertNotNil(store.lastError)
        }
    }

    func testClearHistoryKeepsSavedExclusions() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            let pending = try store.begin(SoftwareUpdateFixtures.candidate, now: now)
            XCTAssertThrowsError(try store.clearHistory())
            try store.finish(pending.id, result: .init(status: .failed, details: "Fixture failure"), now: now)
            try store.exclude([SoftwareUpdateFixtures.candidate], now: now)
            try store.clearHistory()
            let reopened = SoftwareUpdateStore(path: path, now: now)
            XCTAssertTrue(reopened.history(now: now).isEmpty)
            XCTAssertTrue(reopened.isExcluded(SoftwareUpdateFixtures.candidate.id))
        }
    }

    func testConcurrentAttemptsDoNotLoseDurableRecords() throws {
        try withFile { path in
            let store = SoftwareUpdateStore(path: path, now: now)
            let instant = now
            DispatchQueue.concurrentPerform(iterations: 20) { _ in
                do {
                    let attempt = try store.begin(SoftwareUpdateFixtures.candidate, now: instant)
                    try store.finish(attempt.id, result: .init(status: .failed, details: "Concurrent fixture"), now: instant)
                } catch { XCTFail(error.localizedDescription) }
            }
            XCTAssertEqual(store.history(now: now).count, 20)
            XCTAssertEqual(SoftwareUpdateStore(path: path, now: now).history(now: now).count, 20)
        }
    }
}
