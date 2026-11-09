import XCTest
@testable import EvictKit

/// The Mac Info window's logic (design IN1): build details, the feature list and the changelog section.
final class InfoContentTests: XCTestCase {

    private let features = """
    Everything Evict can do.

    | SrNo. | Area | What it does | Since | Windows | Mac |
    |---|---|---|---|---|---|
    | 1 | Programs | Every installed program. | 1.0.0 | Yes | Yes |
    | 2 | Windows Apps | Store apps. | 1.0.0 | Yes | Not yet |
    | 3 | Info window | Shows this build. | 4.2.0 | Yes | Yes |
    | x | Broken | Not a number. | 1.0.0 | Yes | Yes |
    """

    func testFeatureRowsAndMacFilter() {
        let rows = InfoContent.parseFeatures(features)
        XCTAssertEqual(rows.map(\.srNo), [1, 2, 3])
        XCTAssertFalse(rows[1].onMac)
        XCTAssertEqual(InfoContent.filter(rows, query: "").map(\.srNo), [1, 3])
        XCTAssertEqual(InfoContent.filter(rows, query: "BUILD").map(\.srNo), [3])
        XCTAssertTrue(InfoContent.parseFeatures("| No. | Area |\n|---|---|\n| 1 | A |").isEmpty)
    }

    func testChangelogSection() {
        let changelog = """
        # Changelog

        ## 1.2.0 — 2026-11-09

        <!-- Evict automatic release; base: mac-v1.1.0 -->

        - Short beta names and **Info** window

        ## 1.1.0 — 2026-11-08

        - Older
        """
        let section = InfoContent.changelogSection(changelog, version: "1.2.0")
        XCTAssertEqual(section?.heading, "1.2.0 — 2026-11-09")
        XCTAssertEqual(section?.lines, ["• Short beta names and Info window"])
        XCTAssertNil(InfoContent.changelogSection(changelog, version: "1.2"))
        XCTAssertNil(InfoContent.changelogSection(changelog, version: nil))
    }

    func testBetaBuildDetails() throws {
        let json = #"{"sourcePr":"41","sourceCommit":"4E68C2A9D1E2F3A4B5C6D7E8F9A0B1C2D3E4F5A6","builtUtc":"2026-11-04T14:25:00Z","releaseTag":"mac-v1.2.0-beta003"}"#
        let details = try JSONDecoder().decode(BuildDetails.self, from: Data(json.utf8))
        let info = BuildInfo(version: "1.2.0-beta003", buildNumber: "7", details: details, installedAs: "app in /Applications")
        XCTAssertTrue(info.isBeta)
        XCTAssertEqual(info.betaNumber, "003")
        XCTAssertEqual(info.coreVersion, "1.2.0")
        XCTAssertEqual(info.subtitle, "Version 1.2.0-beta003 · Beta · Mac")
        XCTAssertEqual(info.betaBanner, "This is a test build. It is beta 003 of Evict for Mac 1.2.0, made from a pull request.")
        XCTAssertEqual(info.commitText, "4e68c2a")
        XCTAssertEqual(info.downloadName, "Evict-1.2.0-beta003-macOS-universal.dmg")
        let india = try XCTUnwrap(TimeZone(secondsFromGMT: 19800))
        XCTAssertEqual(info.builtOnText(timeZone: india), "04-Nov-2026, 19:55")
        XCTAssertEqual(info.rows(timeZone: india).map { $0.item },
                       ["Version", "Channel", "Made from", "Commit", "Built on", "GitHub tag", "Download", "Installed as"])
        XCTAssertEqual(info.copyText(system: "macOS 15.1 arm64"),
                       "Evict for Mac 1.2.0-beta003 (Beta) · PR #41 · 4e68c2a · built 2026-11-04 14:25 UTC\nmacOS 15.1 arm64 · app in /Applications")
    }

    func testLocalBuildAndMalformedDetails() {
        let local = BuildInfo(version: "1.1.0", buildNumber: "6", details: BuildDetails(), installedAs: "app in /Applications")
        XCTAssertTrue(local.isLocalBuild)
        XCTAssertFalse(local.isBeta)
        XCTAssertNil(local.betaBanner)
        XCTAssertEqual(local.rows().first { $0.item == "Commit" }?.value, BuildInfo.localBuild)
        let bad = BuildDetails(sourcePr: "PR-7", sourceCommit: "zz", builtUtc: "yesterday", releaseTag: "win-v4.2.0")
        XCTAssertEqual(bad, BuildDetails())
        XCTAssertEqual(BuildDetails.load(from: nil), BuildDetails())
    }

    func testUnreservedOrLongBetaHasNoNumber() {
        for version in ["1.2.0-beta000", "1.2.0-beta.40.12.3"] {
            let info = BuildInfo(version: version, buildNumber: "7", details: BuildDetails(), installedAs: "app in /Applications")
            XCTAssertTrue(info.isBeta)
            XCTAssertNil(info.betaNumber)
            XCTAssertEqual(info.betaBanner, "This is a test build of Evict for Mac 1.2.0, made from a pull request.")
        }
    }

    func testRepositoryFeatureListParses() throws {
        // Tests run from the package folder (macos/); the feature list lives in ../docs.
        let url = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .appendingPathComponent("../../../docs/FEATURES.md").standardizedFileURL
        let rows = InfoContent.parseFeatures(try String(contentsOf: url, encoding: .utf8))
        XCTAssertGreaterThanOrEqual(rows.count, 40)
        XCTAssertEqual(rows.map(\.srNo), Array(1...rows.count))
        XCTAssertTrue(rows.contains { $0.area == "Info window" && $0.onMac })
    }
}
