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

    func testFailuresAreNotSubtractedFromOtherSuccessfulPaths() {
        var result = RemovalResult()
        result.trashed = ["/success"]
        result.stillPresent = ["/failure"]
        XCTAssertEqual(result.succeededCount, 1)
        result.trashed = []
        XCTAssertEqual(result.succeededCount, 0)
    }

    func testBundleIsSelectedButSiblingVendorDataAlwaysRequiresManualChoice() {
        let bundle = LeftoverItem(path: "/Applications/Acme Writer.app", kind: .appBundle, confidence: .high,
                                  sizeBytes: 500, reason: "Application", needsAdmin: true)
        let sibling = LeftoverItem(path: NSHomeDirectory() + "/Library/Preferences/com.acme.other.plist",
                                   kind: .preferences, confidence: .medium, sizeBytes: 10,
                                   reason: "Same vendor", isSharedVendorMatch: true)
        let loose = LeftoverItem(path: NSHomeDirectory() + "/Library/Caches/AcmeWriter Extras", kind: .caches,
                                 confidence: .low, sizeBytes: 20, reason: "Name only")
        let plan = UninstallPlan(app: app("com.acme.writer"), bundleItem: bundle,
                                 leftovers: [sibling, loose], homebrewCask: nil)
        XCTAssertEqual(plan.defaultSelection, [bundle.id])
        var settings = Settings()
        settings.preselectLowConfidence = true
        XCTAssertEqual(plan.initialSelection(settings: settings), [bundle.id, loose.id])
        settings.showLowConfidenceItems = false
        XCTAssertEqual(plan.initialSelection(settings: settings), [bundle.id])
    }

    func testOwnedCommandLineLinksAreRemovedBeforeTheApplication() {
        let bundle = LeftoverItem(path: "/Applications/Acme Writer.app", kind: .appBundle, confidence: .high,
                                  sizeBytes: 500, reason: "Application")
        let link = LeftoverItem(path: "/usr/local/bin/acme", kind: .binary, confidence: .high,
                                sizeBytes: 0, reason: "Owned shortcut", ownedBundlePath: bundle.path)
        let plan = UninstallPlan(app: app("com.acme.writer"), bundleItem: bundle, leftovers: [link], homebrewCask: nil)
        XCTAssertEqual(plan.removalItems(selection: [bundle.id, link.id]).map(\.id), [link.id, bundle.id])
    }

    func testByteFormatting() {
        XCTAssertEqual(ByteFormat.string(0), "—")
        XCTAssertFalse(ByteFormat.string(2_000_000).isEmpty)
    }
}
