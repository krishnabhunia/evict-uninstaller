import XCTest
@testable import EvictKit

/// The most important tests in the project: these decide what Evict is allowed to delete.
final class SafePathsTests: XCTestCase {
    private var home: String { NSHomeDirectory() }

    func testRefusesSystemLocations() {
        for path in ["/", "/System", "/System/Library/CoreServices", "/bin/ls", "/usr/lib/libSystem.dylib",
                     "/Library/Apple/System", "/Applications/Utilities/Terminal.app", "/Library/Security/abc"] {
            XCTAssertFalse(SafePaths.isAllowed(path), "should refuse \(path)")
        }
    }

    func testRefusesUserData() {
        for folder in ["Documents", "Desktop", "Downloads", "Pictures", "Library/Mail", "Library/Keychains"] {
            XCTAssertFalse(SafePaths.isAllowed("\(home)/\(folder)/something"), "should refuse \(folder)")
        }
    }

    func testRefusesTheRootsThemselves() {
        for path in ["\(home)/Library", "\(home)/Library/Caches", "\(home)/Library/Preferences",
                     "/Library/Application Support", "/Applications", "/usr/local/bin", "\(home)"] {
            XCTAssertFalse(SafePaths.isAllowed(path), "should refuse the root \(path)")
        }
    }

    func testAllowsRealLeftovers() {
        for path in ["\(home)/Library/Application Support/Acme",
                     "\(home)/Library/Caches/com.acme.app",
                     "\(home)/Library/Preferences/com.acme.app.plist",
                     "\(home)/Library/Containers/com.acme.app",
                     "\(home)/Library/LaunchAgents/com.acme.helper.plist",
                     "/Library/Application Support/Acme",
                     "/Library/LaunchDaemons/com.acme.daemon.plist",
                     "/Library/PrivilegedHelperTools/com.acme.helper",
                     "/Applications/Acme.app"] {
            XCTAssertTrue(SafePaths.isAllowed(path), "should allow \(path)")
        }
    }

    func testRefusesTraversalAndRelativePaths() {
        XCTAssertFalse(SafePaths.isAllowed("Library/Caches/com.acme.app"))
        XCTAssertFalse(SafePaths.isAllowed("\(home)/Library/Caches/../../Documents"))
    }

    func testUserLocationsAreNotClassifiedAsAdministratorOnly() {
        XCTAssertFalse(SafePaths.needsAdmin("\(home)/Library/Caches/com.acme.app"))
    }

    func testWritableParentOutsideHomeDoesNotRequireAdministrator() throws {
        let parent = FileManager.default.temporaryDirectory.appendingPathComponent("EvictPermissionTests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: parent) }
        XCTAssertFalse(SafePaths.needsAdmin(parent.appendingPathComponent("Fixture.app").path))
    }

    func testOwnedAppleSiliconLinksHaveNarrowPermission() {
        let path = "/opt/homebrew/bin/acme"
        XCTAssertFalse(SafePaths.check(path).isAllowed, "Arbitrary Homebrew files must remain protected")
        XCTAssertTrue(SafePaths.checkOwnedBinaryLink(path,
                        destination: "/Applications/Acme.app/Contents/MacOS/acme",
                        bundlePath: "/Applications/Acme.app").isAllowed)
        XCTAssertTrue(SafePaths.checkOwnedBinaryLink(path,
                        destination: "../../../Applications/Acme.app/Contents/MacOS/acme",
                        bundlePath: "/Applications/Acme.app").isAllowed)
        XCTAssertFalse(SafePaths.checkOwnedBinaryLink(path,
                         destination: "/Applications/Other.app/Contents/MacOS/acme",
                         bundlePath: "/Applications/Acme.app").isAllowed)
        XCTAssertFalse(SafePaths.checkOwnedBinaryLink("/opt/homebrew/lib/acme",
                         destination: "/Applications/Acme.app/Contents/MacOS/acme",
                         bundlePath: "/Applications/Acme.app").isAllowed)
        XCTAssertFalse(SafePaths.checkOwnedBinaryLink(path,
                         destination: "/System/Applications/Calculator.app/Contents/MacOS/Calculator",
                         bundlePath: "/System/Applications/Calculator.app").isAllowed)
    }

    func testNormalizeAndIsInside() {
        XCTAssertEqual(SafePaths.normalize("/Library//Caches/"), "/Library/Caches")
        XCTAssertTrue(SafePaths.isInside("/Library/Caches/x", root: "/Library/Caches"))
        XCTAssertFalse(SafePaths.isInside("/Library/CachesOther/x", root: "/Library/Caches"))
    }
}
