import XCTest
@testable import EvictKit

final class NameMatchingTests: XCTestCase {

    func testNormalize() {
        XCTAssertEqual(NameMatching.normalize("Visual Studio Code"), "visualstudiocode")
        XCTAssertEqual(NameMatching.normalize("com.acme.App-1"), "comacmeapp1")
    }

    func testTokens() {
        XCTAssertEqual(NameMatching.tokens("Two Button App"), ["two", "button", "app"])
        XCTAssertEqual(NameMatching.tokens("TwoButtonApp"), ["two", "button", "app"])
        XCTAssertEqual(NameMatching.tokens("VLC media player"), ["vlc", "media", "player"])
    }

    func testStrongTokensDropFillerWords() {
        XCTAssertEqual(NameMatching.strongTokens("Acme Player Pro"), ["acme"])
        XCTAssertEqual(NameMatching.strongTokens("Acme Writer"), ["acme", "writer"])
    }

    func testBundleIdentifierMatching() {
        XCTAssertTrue(NameMatching.matchesBundleIdentifier("com.acme.writer", bundleIdentifier: "com.acme.writer"))
        XCTAssertTrue(NameMatching.matchesBundleIdentifier("com.acme.writer.helper", bundleIdentifier: "com.acme.writer"))
        XCTAssertTrue(NameMatching.matchesBundleIdentifier("com.acme.writer.plist", bundleIdentifier: "com.acme.writer"))
        // A different product whose identifier merely starts with the same letters.
        XCTAssertFalse(NameMatching.matchesBundleIdentifier("com.acme.writerstudio", bundleIdentifier: "com.acme.writer"))
        XCTAssertFalse(NameMatching.matchesBundleIdentifier("com.other.writer", bundleIdentifier: "com.acme.writer"))
    }

    func testVendorPrefixMatching() {
        XCTAssertTrue(NameMatching.matchesVendorPrefix("com.acme.other", bundleIdentifierPrefix: "com.acme"))
        XCTAssertFalse(NameMatching.matchesVendorPrefix("com.acmecorp.other", bundleIdentifierPrefix: "com.acme"))
        XCTAssertFalse(NameMatching.matchesVendorPrefix("com.acme.other", bundleIdentifierPrefix: "com"))
    }

    func testAppNameMatching() {
        XCTAssertTrue(NameMatching.matchesAppName("Acme Writer", appName: "Acme Writer"))
        XCTAssertTrue(NameMatching.matchesAppName("com.acme.writer", appName: "Acme Writer"))
        XCTAssertTrue(NameMatching.matchesAppName("AcmeWriter Support", appName: "Acme Writer"))
        // Only a filler word in common – must not match.
        XCTAssertFalse(NameMatching.matchesAppName("Media Player", appName: "Acme Player"))
        XCTAssertFalse(NameMatching.matchesAppName("Helper", appName: "Acme Helper"))
        // Unrelated app of another vendor.
        XCTAssertFalse(NameMatching.matchesAppName("com.other.notes", appName: "Acme Writer"))
        // Too short a name never matches.
        XCTAssertFalse(NameMatching.matchesAppName("com.acme.anything", appName: "Go"))
    }

    func testVendorToken() {
        XCTAssertEqual(NameMatching.vendorToken(fromBundleIdentifier: "com.acme.writer"), "acme")
        XCTAssertNil(NameMatching.vendorToken(fromBundleIdentifier: "com.app.writer"))
        XCTAssertNil(NameMatching.vendorToken(fromBundleIdentifier: "single"))
    }
}
