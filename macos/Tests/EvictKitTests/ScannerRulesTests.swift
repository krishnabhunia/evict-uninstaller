import XCTest
@testable import EvictKit

final class ScannerRulesTests: XCTestCase {
    private let scanner = LeftoverScanner()

    private func target(name: String = "Acme Writer",
                        bundleID: String? = "com.acme.writer",
                        prefix: String? = "com.acme",
                        bundlePath: String? = "/Applications/Acme Writer.app") -> LeftoverScanner.Target {
        LeftoverScanner.Target(name: name, bundleIdentifier: bundleID, bundleIdentifierPrefix: prefix, bundlePath: bundlePath)
    }

    func testStripsKnownSuffixes() {
        XCTAssertEqual(scanner.strippedName("com.acme.writer.plist"), "com.acme.writer")
        XCTAssertEqual(scanner.strippedName("com.acme.writer.savedState"), "com.acme.writer")
        XCTAssertEqual(scanner.strippedName("Acme Writer.app"), "Acme Writer")
        XCTAssertEqual(scanner.strippedName("com.acme.writer.ABCDEF01-1234-5678-9ABC-DEF012345678"), "com.acme.writer")
    }

    func testBundleIdMatchIsHighConfidence() {
        let item = scanner.evaluate(entry: "com.acme.writer.plist",
                                    path: NSHomeDirectory() + "/Library/Preferences/com.acme.writer.plist",
                                    kind: .preferences, target: target())
        XCTAssertEqual(item?.confidence, .high)
    }

    func testVendorFolderIsMediumConfidence() {
        let item = scanner.evaluate(entry: "com.acme.otherproduct",
                                    path: NSHomeDirectory() + "/Library/Caches/com.acme.otherproduct",
                                    kind: .caches, target: target())
        XCTAssertEqual(item?.confidence, .medium)
    }

    func testLooseNameMatchIsLowConfidence() {
        let item = scanner.evaluate(entry: "AcmeWriter Updates",
                                    path: NSHomeDirectory() + "/Library/Application Support/AcmeWriter Updates",
                                    kind: .supportFiles, target: target())
        XCTAssertEqual(item?.confidence, .low)
    }

    func testExactFolderNameIsMediumConfidence() {
        let item = scanner.evaluate(entry: "Acme Writer",
                                    path: NSHomeDirectory() + "/Library/Application Support/Acme Writer",
                                    kind: .supportFiles, target: target())
        XCTAssertEqual(item?.confidence, .medium)
    }

    func testUnrelatedEntriesAreIgnored() {
        for entry in ["com.apple.finder.plist", "Google", "Microsoft Word", "com.other.writer", "Logs"] {
            let item = scanner.evaluate(entry: entry,
                                        path: NSHomeDirectory() + "/Library/Application Support/" + entry,
                                        kind: .supportFiles, target: target())
            XCTAssertNil(item, "\(entry) must not be treated as a leftover of Acme Writer")
        }
    }

    func testNoBundleIdentifierStillMatchesByName() {
        let item = scanner.evaluate(entry: "Acme Writer",
                                    path: NSHomeDirectory() + "/Library/Caches/Acme Writer",
                                    kind: .caches,
                                    target: target(bundleID: nil, prefix: nil, bundlePath: nil))
        XCTAssertEqual(item?.confidence, .medium)
    }
}
