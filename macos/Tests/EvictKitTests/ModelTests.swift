import XCTest
@testable import EvictKit

final class ModelTests: XCTestCase {

    private func app(_ id: String?) -> InstalledApp {
        InstalledApp(bundlePath: "/Applications/Acme Writer.app", name: "Acme Writer",
                     bundleIdentifier: id, version: "1.0", source: .dragInstalled, sizeBytes: 100)
    }

    func testBundleIdentifierPrefix() {
        XCTAssertEqual(app("com.acme.writer").bundleIdentifierPrefix, "com.acme")
        XCTAssertNil(app("single").bundleIdentifierPrefix)
        XCTAssertNil(app(nil).bundleIdentifierPrefix)
    }

    func testSystemAppsAreNotRemovable() {
        XCTAssertFalse(AppSource.system.isRemovable)
        XCTAssertTrue(AppSource.dragInstalled.isRemovable)
    }

    func testPlanGroupsAndDefaultSelection() {
        let bundle = LeftoverItem(path: "/Applications/Acme Writer.app", kind: .appBundle, confidence: .high,
                                  sizeBytes: 500, reason: "The application itself")
        let prefs = LeftoverItem(path: NSHomeDirectory() + "/Library/Preferences/com.acme.writer.plist",
                                 kind: .preferences, confidence: .high, sizeBytes: 10, reason: "Bundle ID")
        let loose = LeftoverItem(path: NSHomeDirectory() + "/Library/Caches/AcmeWriterish",
                                 kind: .caches, confidence: .low, sizeBytes: 20, reason: "Name looks like the app's")
        let admin = LeftoverItem(path: "/Library/Application Support/Acme", kind: .supportFiles, confidence: .high,
                                 sizeBytes: 30, reason: "Vendor folder", needsAdmin: true)
        let plan = UninstallPlan(app: app("com.acme.writer"), bundleItem: bundle,
                                 leftovers: [prefs, loose, admin], homebrewCask: nil)

        XCTAssertEqual(plan.totalBytes, 560)
        XCTAssertEqual(plan.groups.map(\.kind), [.appBundle, .supportFiles, .preferences, .caches])
        // Low confidence is never pre-ticked, and neither is anything needing an administrator.
        XCTAssertEqual(plan.defaultSelection, [bundle.id, prefs.id])
    }

    func testRemovalResultCounting() {
        var result = RemovalResult()
        result.trashed = ["/a", "/b", "/c"]
        result.stillPresent = ["/c"]
        XCTAssertEqual(result.succeededCount, 2)
    }

    func testByteFormatting() {
        XCTAssertEqual(ByteFormat.string(0), "—")
        XCTAssertFalse(ByteFormat.string(2_000_000).isEmpty)
    }
}
