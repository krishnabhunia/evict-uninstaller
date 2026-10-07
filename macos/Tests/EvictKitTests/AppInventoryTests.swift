import XCTest
@testable import EvictKit

final class AppInventoryTests: XCTestCase {
    private func withDirectory(_ test: (URL) throws -> Void) throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("EvictInventoryTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try test(directory)
    }

    func testOverlappingApplicationAndSetappRootsProduceOneBundle() throws {
        try withDirectory { directory in
            let applications = directory.appendingPathComponent("Applications")
            let setapp = applications.appendingPathComponent("Setapp")
            let bundle = setapp.appendingPathComponent("Fixture.app")
            try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
            let paths = AppInventory().bundlePaths(includeSystemApps: false, folders: [applications.path, setapp.path])
            XCTAssertEqual(paths, [bundle.path])
        }
    }

    func testAppleAppStoreApplicationsRemainRemovable() throws {
        try withDirectory { directory in
            let bundle = directory.appendingPathComponent("Pages.app")
            let receipt = bundle.appendingPathComponent("Contents/_MASReceipt/receipt")
            try FileManager.default.createDirectory(at: receipt.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data().write(to: receipt)
            let source = AppInventory().source(bundlePath: bundle.path, bundleID: "com.apple.iWork.Pages", receipts: [], casks: [])
            XCTAssertEqual(source, .appStore)
            XCTAssertTrue(source.isRemovable)
        }
        XCTAssertEqual(AppInventory().source(bundlePath: "/System/Applications/Calculator.app",
                                            bundleID: "com.apple.calculator", receipts: [], casks: []), .system)
    }

    func testDroppedTargetsMustBeApplicationBundles() throws {
        try withDirectory { directory in
            XCTAssertNil(AppInventory.validBundlePath(directory.path))
            let bundle = directory.appendingPathComponent("Fixture.app")
            try FileManager.default.createDirectory(at: bundle.appendingPathComponent("Contents"), withIntermediateDirectories: true)
            XCTAssertNil(AppInventory.validBundlePath(bundle.path))
            let plist = try PropertyListSerialization.data(fromPropertyList: ["CFBundleIdentifier": "com.evict.fixture"],
                                                          format: .xml, options: 0)
            try plist.write(to: bundle.appendingPathComponent("Contents/Info.plist"))
            XCTAssertEqual(AppInventory.validBundlePath(bundle.path), bundle.path)
        }
    }
}
