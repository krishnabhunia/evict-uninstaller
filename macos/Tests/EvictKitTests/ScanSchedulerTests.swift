import XCTest
@testable import EvictKit

final class ScanSchedulerTests: XCTestCase {
    let executable = "/Applications/Evict.app/Contents/MacOS/Evict"

    func testBackgroundScanRequiresEnabledMatchingSavedConfiguration() throws {
        var settings = Settings()
        settings.scanSchedule = ScanSchedule(frequency: .daily, token: UUID().uuidString)
        let arguments = [executable, "--scheduled-scan", "--schedule-token", settings.scanSchedule.token]
        let plist = try ScanScheduler.propertyList(schedule: settings.scanSchedule, executable: executable)
        XCTAssertTrue(ScheduledScanRunner.authorized(arguments: arguments, settings: settings, plist: plist))
        XCTAssertFalse(ScheduledScanRunner.authorized(arguments: arguments, settings: Settings(), plist: plist))
        XCTAssertFalse(ScheduledScanRunner.authorized(arguments: arguments + ["--force"], settings: settings, plist: plist))
        var wrong = arguments
        wrong[3] = UUID().uuidString
        XCTAssertFalse(ScheduledScanRunner.authorized(arguments: wrong, settings: settings, plist: plist))
        settings.scanSchedule.hour += 1
        XCTAssertFalse(ScheduledScanRunner.authorized(arguments: arguments, settings: settings, plist: plist))
    }

    func testWeeklyAndDailyCalendarAndArgumentEscaping() throws {
        let path = "/Applications/A folder's $(literal)/Evict.app/Contents/MacOS/Evict"
        let schedule = ScanSchedule(frequency: .weekly, hour: 7, minute: 45, weekday: 0, token: UUID().uuidString)
        let data = try ScanScheduler.propertyList(schedule: schedule, executable: path)
        let plist = try XCTUnwrap(PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any])
        XCTAssertEqual((plist["ProgramArguments"] as? [String])?.first, path)
        XCTAssertEqual(plist["StartCalendarInterval"] as? [String: Int], ["Hour": 7, "Minute": 45, "Weekday": 0])
        XCTAssertFalse(plist["RunAtLoad"] as? Bool ?? true)
        XCTAssertNil(ScanSchedule(frequency: .daily).calendar["Weekday"])
    }

    func testRejectsForeignJobWithoutCallingLaunchctl() throws {
        let folder = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let path = folder.appendingPathComponent("scan.plist")
        let data = try PropertyListSerialization.data(fromPropertyList: ["Label": "someone.else"], format: .xml, options: 0)
        try data.write(to: path)
        var called = false
        let scheduler = ScanScheduler(plistPath: path.path, userID: 501) { _, _ in
            called = true
            return .init(status: 0, stdout: "", stderr: "")
        }
        XCTAssertThrowsError(try scheduler.apply(ScanSchedule(), executable: executable) { _ in XCTFail("Must not save") })
        XCTAssertFalse(called)
        XCTAssertEqual(try Data(contentsOf: path), data)
    }

    func testPersistenceFailureRestoresPreviousRegistration() throws {
        let folder = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let path = folder.appendingPathComponent("scan.plist")
        let previous = ScanSchedule(frequency: .daily, token: UUID().uuidString)
        let data = try ScanScheduler.propertyList(schedule: previous, executable: executable)
        try data.write(to: path)
        var loaded = true
        let scheduler = ScanScheduler(plistPath: path.path, userID: 501) { _, args in
            if args[0] == "bootout" { loaded = false }
            if args[0] == "bootstrap" { loaded = true }
            return .init(status: args[0] == "print" && !loaded ? 3 : 0, stdout: "", stderr: loaded ? "" : "Could not find service")
        }
        XCTAssertThrowsError(try scheduler.apply(ScanSchedule(), executable: executable) { _ in
            throw ScanScheduleError.registration("Disk full")
        })
        XCTAssertTrue(loaded)
        XCTAssertEqual(try Data(contentsOf: path), data)
    }

    func testInvalidScheduleCannotCreateAgent() {
        XCTAssertThrowsError(try ScanScheduler.propertyList(schedule: ScanSchedule(frequency: .daily, hour: 24, token: UUID().uuidString), executable: executable))
        XCTAssertFalse(ScanScheduler.isOwned(Data("not a plist".utf8)))
    }

    func testQueryFailureCannotBeReportedAsDisabled() {
        let scheduler = ScanScheduler(plistPath: "/tmp/evict-missing-\(UUID().uuidString).plist", userID: 501) { _, _ in
            .init(status: 124, stdout: "", stderr: "timed out")
        }
        XCTAssertThrowsError(try scheduler.apply(ScanSchedule(), executable: executable) { _ in XCTFail("Must not save") })
    }

    func testEnablingPersistsOnlyAfterRegistrationIsVerified() throws {
        let folder = FileManager.default.temporaryDirectory.resolvingSymlinksInPath().appendingPathComponent(UUID().uuidString)
        let app = folder.appendingPathComponent("Applications/Evict.app/Contents/MacOS/Evict")
        try FileManager.default.createDirectory(at: app.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: app)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: app.path)
        defer { try? FileManager.default.removeItem(at: folder) }
        let path = folder.appendingPathComponent("LaunchAgents/scan.plist")
        var loaded = false
        var verified = false
        var saved: ScanSchedule?
        let scheduler = ScanScheduler(plistPath: path.path, userID: 501,
                                      applicationRoots: [folder.appendingPathComponent("Applications").path]) { _, args in
            if args[0] == "bootstrap" { loaded = true; return .init(status: 0, stdout: "", stderr: "") }
            if args[0] == "print" && loaded {
                let data = try! Data(contentsOf: path)
                let plist = try! PropertyListSerialization.propertyList(from: data, format: nil) as! [String: Any]
                verified = true
                return .init(status: 0, stdout: (plist["ProgramArguments"] as! [String]).joined(separator: "\n"), stderr: "")
            }
            return .init(status: 113, stdout: "", stderr: "Could not find service")
        }
        let applied = try scheduler.apply(ScanSchedule(frequency: .weekly, hour: 9, minute: 15, weekday: 6), executable: app.path) {
            XCTAssertTrue(verified)
            saved = $0
        }
        XCTAssertTrue(loaded)
        XCTAssertEqual(applied, saved)
        XCTAssertNotNil(UUID(uuidString: applied.token))
        XCTAssertTrue(ScanScheduler.isOwned(try Data(contentsOf: path)))
    }
}
