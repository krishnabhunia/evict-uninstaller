import XCTest
@testable import EvictKit

final class LaunchItemsTests: XCTestCase {

    func testOrphanDetection() {
        XCTAssertTrue(LaunchItems.isOrphan(program: "/Applications/Gone.app/Contents/MacOS/Gone"))
        XCTAssertFalse(LaunchItems.isOrphan(program: "/usr/bin/true"))
        XCTAssertFalse(LaunchItems.isOrphan(program: "/System/Library/CoreServices/Anything"))
        XCTAssertFalse(LaunchItems.isOrphan(program: nil))
        XCTAssertFalse(LaunchItems.isOrphan(program: "some-relative-command"))
    }

    func testLaunchdProgramPrefersProgramKey() {
        XCTAssertEqual(Plist.launchdProgram(["Program": "/bin/echo", "ProgramArguments": ["/bin/ls", "-l"]]), "/bin/echo")
        XCTAssertEqual(Plist.launchdProgram(["ProgramArguments": ["/bin/ls", "-l"]]), "/bin/ls")
        XCTAssertNil(Plist.launchdProgram(["Label": "com.acme.job"]))
    }

    func testPlistHelpers() {
        XCTAssertEqual(Plist.string(["A": "value"], "A"), "value")
        XCTAssertNil(Plist.string(["A": ""], "A"))
        XCTAssertTrue(Plist.bool(["RunAtLoad": true], "RunAtLoad"))
        XCTAssertFalse(Plist.bool([:], "RunAtLoad"))
    }
}
