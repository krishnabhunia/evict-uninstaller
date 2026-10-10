import XCTest
@testable import EvictKit

final class ApplicationsTableTests: XCTestCase {

    private func app(_ name: String, version: String? = "1.0", size: Int64 = 0, source: AppSource = .dragInstalled,
                     installed: Date? = nil, bundleID: String? = nil) -> InstalledApp {
        InstalledApp(bundlePath: "/Applications/\(name).app", name: name, bundleIdentifier: bundleID,
                     version: version, source: source, sizeBytes: size, installedDate: installed)
    }

    func testSortsByNameNaturally() {
        let apps = [app("app10"), app("App2"), app("Zed"), app("acrobat")]
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .name, ascending: true).map(\.name), ["acrobat", "App2", "app10", "Zed"])
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .name, ascending: false).map(\.name), ["Zed", "app10", "App2", "acrobat"])
    }

    func testVersionsCompareAsNumbers() {
        let apps = [app("A", version: "1.10.0"), app("B", version: "1.9.12"), app("C", version: nil)]
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .version, ascending: true).map(\.name), ["C", "B", "A"])
    }

    func testSizeAndTiesFallBackToName() {
        let apps = [app("Beta", size: 10), app("Alpha", size: 10), app("Big", size: 99)]
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .size, ascending: false).map(\.name), ["Big", "Alpha", "Beta"])
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .size, ascending: true).map(\.name), ["Alpha", "Beta", "Big"])
    }

    func testMissingDatesSortAsOldest() {
        let now = Date()
        let apps = [app("Old", installed: now.addingTimeInterval(-86_400)), app("Unknown"), app("New", installed: now)]
        XCTAssertEqual(ApplicationsTable.sorted(apps, by: .installed, ascending: false).map(\.name), ["New", "Old", "Unknown"])
    }

    func testColumnDefaults() {
        XCTAssertEqual(AppColumn.allCases.filter { !$0.visibleByDefault }, [.installed, .lastUsed, .location])
        XCTAssertEqual(AppColumn(rawValue: "lastUsed"), .lastUsed)   // stored in defaults: keep stable
    }

    func testSelectionSummary() {
        let apps = [app("Claude", size: 928_100_000), app("Code", size: 913_900_000), app("Cursor", size: 900_100_000), app("Docker", size: 1)]
        XCTAssertEqual(ApplicationsTable.selectionSummary(apps, selected: []), "Nothing selected")
        let one = ApplicationsTable.selectionSummary(apps, selected: [apps[1].id])
        XCTAssertTrue(one.hasPrefix("1 selected — Code · "), one)
        let three = ApplicationsTable.selectionSummary(apps, selected: Set(apps.prefix(3).map(\.id)))
        XCTAssertTrue(three.hasPrefix("3 selected — Claude, Code, Cursor · "), three)
        let four = ApplicationsTable.selectionSummary(apps, selected: Set(apps.map(\.id)))
        XCTAssertTrue(four.hasPrefix("4 selected — Claude, Code, Cursor and 1 more · "), four)
    }

    func testUninstallLabel() {
        XCTAssertEqual(ApplicationsTable.uninstallLabel(count: 1), "Uninstall…")
        XCTAssertEqual(ApplicationsTable.uninstallLabel(count: 3), "Uninstall 3 apps…")
    }

    func testQueueSkipsSystemAppsAndDuplicatesAndAdvances() {
        let a = app("A"), b = app("B"), safari = app("Safari", source: .system)
        var queue = UninstallQueue([a, safari, b, a])
        XCTAssertEqual(queue.apps.map(\.name), ["A", "B"])
        XCTAssertTrue(queue.isBatch)
        XCTAssertEqual(queue.current?.name, "A")
        XCTAssertEqual(queue.progressText, "App 1 of 2")
        XCTAssertEqual(queue.advance()?.name, "B")
        XCTAssertEqual(queue.progressText, "App 2 of 2")
        XCTAssertNil(queue.advance())
        XCTAssertTrue(queue.isEmpty)
        XCTAssertEqual(queue.progressText, "")
        XCTAssertNil(queue.advance())
    }

    func testSingleAppQueueHasNoProgressText() {
        var queue = UninstallQueue([app("Solo")])
        XCTAssertFalse(queue.isBatch)
        XCTAssertEqual(queue.progressText, "")
        queue.clear()
        XCTAssertTrue(queue.isEmpty)
    }
}
