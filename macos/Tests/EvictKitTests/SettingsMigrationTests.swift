import XCTest
@testable import EvictKit

final class SettingsMigrationTests: XCTestCase {
    func testOlderPreferencesSurviveNewFields() throws {
        let data = Data(#"{"appearance":"Dark","includeSystemApps":true,"checkForUpdates":false,"preselectLowConfidence":true}"#.utf8)
        let settings = try JSONDecoder().decode(Settings.self, from: data)
        XCTAssertEqual(settings.appearance, "Dark")
        XCTAssertTrue(settings.includeSystemApps)
        XCTAssertFalse(settings.checkForUpdates)
        XCTAssertTrue(settings.preselectLowConfidence)
        XCTAssertEqual(settings.scanSchedule.frequency, .never)
        XCTAssertEqual(settings.settingsVersion, 2)
    }

    func testFailedSaveDoesNotChangeEffectivePreferences() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let blocked = directory.appendingPathComponent("not-a-folder")
        try Data("block".utf8).write(to: blocked)
        let store = SettingsStore(path: blocked.appendingPathComponent("settings.json").path)
        XCTAssertFalse(store.update { $0.appearance = "Dark" })
        XCTAssertEqual(store.current.appearance, "System")
        XCTAssertNotNil(store.lastError)
    }

    func testSavedScheduleAndThemeRoundTrip() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let path = directory.appendingPathComponent("settings.json").path
        let store = SettingsStore(path: path)
        let schedule = ScanSchedule(frequency: .weekly, hour: 15, minute: 20, weekday: 4, token: UUID().uuidString)
        XCTAssertTrue(store.update { $0.appearance = "Light"; $0.scanSchedule = schedule })
        XCTAssertEqual(SettingsStore(path: path).current.scanSchedule, schedule)
        XCTAssertEqual(SettingsStore(path: path).current.appearance, "Light")
    }

    func testCorruptPreferencesArePreservedUntilExplicitReset() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let path = directory.appendingPathComponent("settings.json")
        let damaged = Data("{unfinished".utf8)
        try damaged.write(to: path)
        let store = SettingsStore(path: path.path)
        XCTAssertNotNil(store.lastError)
        XCTAssertFalse(store.update { $0.appearance = "Dark" })
        XCTAssertEqual(try Data(contentsOf: path), damaged)
        XCTAssertTrue(store.reset())
        XCTAssertNil(store.lastError)
        XCTAssertEqual(SettingsStore(path: path.path).current.appearance, "System")
    }
}
