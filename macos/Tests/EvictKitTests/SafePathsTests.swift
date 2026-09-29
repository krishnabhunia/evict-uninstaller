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

    func testNeedsAdminOutsideHome() {
        XCTAssertTrue(SafePaths.needsAdmin("/Library/Application Support/Acme"))
        XCTAssertFalse(SafePaths.needsAdmin("\(home)/Library/Caches/com.acme.app"))
    }

    func testNormalizeAndIsInside() {
        XCTAssertEqual(SafePaths.normalize("/Library//Caches/"), "/Library/Caches")
        XCTAssertTrue(SafePaths.isInside("/Library/Caches/x", root: "/Library/Caches"))
        XCTAssertFalse(SafePaths.isInside("/Library/CachesOther/x", root: "/Library/Caches"))
    }
}
