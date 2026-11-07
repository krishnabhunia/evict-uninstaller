import XCTest
@testable import EvictKit

final class ShellTimeoutTests: XCTestCase {
    func testTermIgnoringCommandIsBoundedAndNotReportedSuccessful() {
        let started = Date()
        let output = Shell.run("/bin/sh", ["-c", "trap '' TERM; while :; do :; done"], timeout: 0.1)
        XCTAssertEqual(output.status, 124)
        XCTAssertFalse(output.ok)
        XCTAssertLessThan(Date().timeIntervalSince(started), 6)
    }

    func testEnvironmentAndLiteralArguments() {
        let output = Shell.run("/bin/sh", ["-c", "printf '%s|%s' \"$EVICT_TEST_VALUE\" \"$1\"", "fixture", "$(literal)"],
                               environment: ["EVICT_TEST_VALUE": "literal value"])
        XCTAssertTrue(output.ok)
        XCTAssertEqual(output.stdout, "literal value|$(literal)")
    }
}
