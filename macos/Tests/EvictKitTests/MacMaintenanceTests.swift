import Foundation
import XCTest
@testable import EvictKit

final class MacMaintenanceTests: XCTestCase {
    private func fixture(_ body: (URL) throws -> Void) throws {
        let url = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("EvictMaintenance-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: url) }
        try body(url)
    }

    private func write(_ data: String, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(data.utf8).write(to: url)
    }

    func testCleanupNeverSelectsRootAppleCachesOrLinks() throws {
        try fixture { directory in
            let root = directory.appendingPathComponent("Caches")
            try write("cache", to: root.appendingPathComponent("org.fixture.app/data"))
            try write("protected", to: root.appendingPathComponent("com.apple.fixture/data"))
            let privateFile = directory.appendingPathComponent("Documents/private.txt")
            try write("personal data", to: privateFile)
            try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("linked"), withDestinationURL: privateFile)
            let scanner = MacCleanupScanner(roots: [.init(path: root.path, category: .caches)], acceptsPath: { _ in true })
            let result = scanner.scan()
            XCTAssertEqual(result.candidates.map(\.name), ["org.fixture.app"])
            XCTAssertEqual(result.totalBytes, 5)
            XCTAssertTrue(scanner.validate(result.candidates[0]))
            XCTAssertFalse(MacMaintenancePaths.isDirectUnaliasedChild(root.path, root: root.path))
        }
    }

    func testCleanupRejectsLinkedRootsAndReplacedCandidates() throws {
        try fixture { directory in
            let root = directory.appendingPathComponent("Caches")
            try write("first", to: root.appendingPathComponent("fixture.log"))
            let scanner = MacCleanupScanner(roots: [.init(path: root.path, category: .logs)], acceptsPath: { _ in true })
            let candidate = try XCTUnwrap(scanner.scan().candidates.first)
            let moved = directory.appendingPathComponent("old.log")
            try FileManager.default.moveItem(atPath: candidate.path, toPath: moved.path)
            try FileManager.default.createSymbolicLink(atPath: candidate.path, withDestinationPath: moved.path)
            XCTAssertFalse(scanner.validate(candidate))
            let alias = directory.appendingPathComponent("Alias")
            try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: root)
            let linked = MacCleanupScanner(roots: [.init(path: alias.path, category: .logs)], acceptsPath: { _ in true }).scan()
            XCTAssertTrue(linked.candidates.isEmpty)
            XCTAssertEqual(linked.warnings.count, 1)
        }
    }

    func testCleanupSizeLimitIsHonestAndDoesNotFollowNestedLink() throws {
        try fixture { directory in
            try write("1234", to: directory.appendingPathComponent("cache/a"))
            try write("12345678", to: directory.appendingPathComponent("private/b"))
            try FileManager.default.createSymbolicLink(at: directory.appendingPathComponent("cache/link"),
                                                       withDestinationURL: directory.appendingPathComponent("private"))
            let measured = MacCleanupScanner.measure(directory.appendingPathComponent("cache").path, limit: 20)
            XCTAssertEqual(measured.bytes, 4)
            XCTAssertFalse(measured.estimated)
            XCTAssertTrue(MacCleanupScanner.measure(directory.appendingPathComponent("cache").path, limit: 0).estimated)
        }
    }

    func testApplicationFiltersDoNotInventUsageOrInstallationDates() {
        let now = Date(timeIntervalSince1970: 2_000_000_000)
        let unknown = InstalledApp(bundlePath: "/Applications/Unknown.app", name: "Unknown", bundleIdentifier: nil,
                                   version: nil, source: .dragInstalled, sizeBytes: 10)
        XCTAssertFalse(MacApplicationFilter.recent.includes(unknown, now: now))
        XCTAssertFalse(MacApplicationFilter.rarelyUsed.includes(unknown, now: now))
        let app = InstalledApp(bundlePath: "/Applications/Fixture.app", name: "Fixture", bundleIdentifier: "org.fixture",
            version: "1", source: .homebrewCask, sizeBytes: 1_073_741_824,
            installedDate: now.addingTimeInterval(-86_400), lastUsedDate: now.addingTimeInterval(-91 * 86_400))
        XCTAssertTrue(MacApplicationFilter.large.includes(app))
        XCTAssertTrue(MacApplicationFilter.recent.includes(app, now: now))
        XCTAssertTrue(MacApplicationFilter.rarelyUsed.includes(app, now: now))
        XCTAssertTrue(MacApplicationFilter.homebrew.includes(app))
        XCTAssertFalse(MacApplicationFilter.appStore.includes(app))
    }

    func testBrowserInventoryChoosesLatestManifestAndPreservesProfileIdentity() throws {
        try fixture { directory in
            let identifier = String(repeating: "a", count: 32)
            let root = directory.appendingPathComponent("Google/Chrome/Default/Extensions/" + identifier)
            try write("{\"name\":\"Old\",\"version\":\"2.0\"}", to: root.appendingPathComponent("2.0_0/manifest.json"))
            try write("{\"name\":\"Fixture\",\"version\":\"10.0\",\"host_permissions\":[\"<all_urls>\"]}",
                      to: root.appendingPathComponent("10.0_0/manifest.json"))
            let result = MacBrowserExtensions.scan(applicationSupport: directory.path)
            XCTAssertEqual(result.extensions.count, 1)
            let item = try XCTUnwrap(result.extensions.first)
            XCTAssertEqual(item.version, "10.0")
            XCTAssertEqual(item.name, "Fixture")
            XCTAssertEqual(item.profile, "Default")
            XCTAssertTrue(item.hasBroadPermissions)
            XCTAssertNil(item.isActive, "Manifest presence does not prove an extension is enabled")
        }
    }

    func testBrowserIdentifiersAndLocalesCannotEscapeProfiles() throws {
        XCTAssertFalse(MacBrowserExtensions.validChromiumIdentifier("../outside"))
        XCTAssertFalse(MacBrowserExtensions.validChromiumIdentifier(String(repeating: "z", count: 32)))
        let item = MacBrowserExtensions.chromium(["name": "__MSG_private__", "default_locale": "../../private"],
            browser: .chrome, profile: "Default", identifier: String(repeating: "a", count: 32), folder: "/tmp/missing")
        XCTAssertEqual(item.name, "__MSG_private__")
    }

    func testFirefoxSkipsSystemAddonsAndRetainsDisabledState() {
        let json: [String: Any] = ["addons": [
            ["id": "builtin", "type": "extension", "isSystem": true],
            ["id": "fixture@example", "type": "extension", "active": false,
             "defaultLocale": ["name": "Fixture"], "userPermissions": ["origins": ["<all_urls>"]]],
            ["id": "theme", "type": "theme"]
        ]]
        let result = MacBrowserExtensions.firefox(json, profile: "work", path: "/tmp/extensions.json")
        XCTAssertEqual(result.count, 1)
        XCTAssertEqual(result.first?.isActive, false)
        XCTAssertEqual(result.first?.name, "Fixture")
        XCTAssertEqual(result.first?.hasBroadPermissions, true)
    }

    private func stamp(_ path: String, version: String = "1") -> MacInstallFileStamp {
        MacInstallFileStamp(path: path, modified: Date(timeIntervalSince1970: 1_000), bytes: 10,
                            bundleVersion: version, isDirectory: true)
    }

    func testInstallMonitorSeparatesAddedUpdatedAndRemovedPaths() {
        let before = MacInstallSnapshot(entries: [stamp("/Applications/Old.app"), stamp("/Applications/Updated.app")])
        let after = MacInstallSnapshot(entries: [stamp("/Applications/New.app"), stamp("/Applications/Updated.app", version: "2")])
        let session = MacInstallMonitor.compare(before: before, after: after)
        XCTAssertEqual(session.changes.map(\.kind), [.added, .removed, .changed])
        XCTAssertEqual(session.addedApps.map(\.path), ["/Applications/New.app"])
        XCTAssertEqual(session.changes.last?.beforeVersion, "1")
        XCTAssertEqual(session.changes.last?.afterVersion, "2")
    }

    func testIncompleteSnapshotsCannotClaimAnAppWasAddedOrRemoved() {
        let before = MacInstallSnapshot(entries: [stamp("/Applications/Old.app")], incomplete: true)
        let after = MacInstallSnapshot(entries: [stamp("/Applications/New.app")], incomplete: true)
        let session = MacInstallMonitor.compare(before: before, after: after)
        XCTAssertTrue(session.changes.isEmpty)
        XCTAssertTrue(session.incomplete)
    }

    func testInstallMonitorCaptureSkipsSymlinksAndLimitsDepth() throws {
        try fixture { directory in
            try write("direct", to: directory.appendingPathComponent("Apps/direct"))
            try write("nested", to: directory.appendingPathComponent("Apps/vendor/deep/file"))
            try write("private", to: directory.appendingPathComponent("Outside/private"))
            try FileManager.default.createSymbolicLink(at: directory.appendingPathComponent("Apps/link"),
                                                       withDestinationURL: directory.appendingPathComponent("Outside"))
            let snapshot = MacInstallMonitor.capture(roots: [directory.appendingPathComponent("Apps").path])
            XCTAssertFalse(snapshot.entries.contains { $0.path.contains("/link") })
            XCTAssertFalse(snapshot.entries.contains { $0.path.hasSuffix("deep/file") })
            XCTAssertFalse(snapshot.incomplete)
            let bounded = MacInstallMonitor.capture(roots: [directory.appendingPathComponent("Apps").path], maximumEntries: 1)
            XCTAssertEqual(bounded.entries.count, 1)
            XCTAssertTrue(bounded.incomplete)
        }
    }

    func testInstallRecordingSurvivesRestartAndCompletesAtomically() throws {
        try fixture { directory in
            let path = directory.appendingPathComponent("monitor.json").path
            let first = MacInstallMonitorStore(path: path)
            try first.begin(MacInstallSnapshot(entries: [stamp("/Applications/Old.app")]))
            XCTAssertThrowsError(try first.begin(MacInstallSnapshot(entries: [])))
            let restarted = MacInstallMonitorStore(path: path)
            XCTAssertNotNil(restarted.recording)
            let finished = try restarted.finish(MacInstallSnapshot(entries: [stamp("/Applications/New.app")]))
            XCTAssertEqual(finished.changes.count, 2)
            let final = MacInstallMonitorStore(path: path)
            XCTAssertNil(final.recording)
            XCTAssertEqual(final.sessions.count, 1)
        }
    }

    func testCorruptRecordingIsPreservedAndCannotBeOverwritten() throws {
        try fixture { directory in
            let url = directory.appendingPathComponent("monitor.json")
            try write("unreadable fixture", to: url)
            let store = MacInstallMonitorStore(path: url.path)
            XCTAssertNotNil(store.lastError)
            XCTAssertThrowsError(try store.begin(MacInstallSnapshot(entries: [])))
            XCTAssertThrowsError(try store.cancelRecording())
            XCTAssertEqual(try String(contentsOf: url), "unreadable fixture")
        }
    }

    func testRestartDoesNotInventChangesFromLostTimestampPrecision() throws {
        try fixture { directory in
            let path = directory.appendingPathComponent("monitor.json").path
            let entry = MacInstallFileStamp(path: "/Applications/Unchanged.app",
                modified: Date(timeIntervalSince1970: 1_800_000_000.123456), bytes: 10, isDirectory: true)
            let before = MacInstallSnapshot(entries: [entry])
            try MacInstallMonitorStore(path: path).begin(before)
            let session = try MacInstallMonitorStore(path: path).finish(MacInstallSnapshot(entries: [entry]))
            XCTAssertTrue(session.changes.isEmpty)
        }
    }

    func testRecordingSaveFailureDoesNotClaimItStarted() throws {
        try fixture { directory in
            let parentFile = directory.appendingPathComponent("not-a-directory")
            try write("keep", to: parentFile)
            let store = MacInstallMonitorStore(path: parentFile.appendingPathComponent("monitor.json").path)
            XCTAssertThrowsError(try store.begin(MacInstallSnapshot(entries: [])))
            XCTAssertNil(store.recording)
            XCTAssertEqual(try String(contentsOf: parentFile), "keep")
        }
    }

    func testSecurityUnknownDoesNotAppearEnabled() {
        XCTAssertEqual(MacSecuritySnapshot.value("permission denied", enabled: "on", disabled: "off"), .unknown)
        XCTAssertEqual(MacSecuritySnapshot.value("FileVault is On.", enabled: "filevault is on", disabled: "filevault is off"), .enabled)
        XCTAssertEqual(MacSecuritySnapshot.value("assessments disabled", enabled: "assessments enabled", disabled: "assessments disabled"), .disabled)
    }
}
