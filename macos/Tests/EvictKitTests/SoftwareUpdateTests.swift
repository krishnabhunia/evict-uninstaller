import XCTest
@testable import EvictKit

enum SoftwareUpdateFixtures {
    static func cask(token: String = "fixture", tap: String = "homebrew/cask", installed: String? = "1.0",
                     available: String = "2.0", appVersion: String? = "1.0", appBuild: String? = nil,
                     pinned: Bool = false, disabled: Bool = false) -> [String: Any] {
        var row: [String: Any] = [
            "token": token, "full_token": token, "tap": tap, "name": ["Fixture App"],
            "version": available, "pinned": pinned, "disabled": disabled,
            "artifacts": [["app": ["Fixture.app"]]], "homepage": "https://example.com/app"
        ]
        row["installed"] = installed.map { $0 as Any } ?? NSNull()
        row["bundle_short_version"] = appVersion.map { $0 as Any } ?? NSNull()
        row["bundle_version"] = appBuild.map { $0 as Any } ?? NSNull()
        return row
    }

    static func json(_ rows: [[String: Any]]) -> String {
        String(data: try! JSONSerialization.data(withJSONObject: ["formulae": [], "casks": rows]), encoding: .utf8)!
    }

    static func inventory(installed: String? = "1.0", available: String = "2.0", appVersion: String? = "1.0", pinned: Bool = false) -> String {
        json([cask(installed: installed, available: available, appVersion: appVersion, pinned: pinned)])
    }

    static func outdated(name: String = "fixture", current: String = "2.0", pinned: Bool = false) -> String {
        json([["name": name, "installed_versions": ["1.0"], "current_version": current, "pinned": pinned]])
    }

    static let candidate = SoftwareUpdateCandidate(caskID: "homebrew/cask/fixture", name: "Fixture App",
                                                   recordedVersion: "1.0", availableVersion: "2.0", appVersion: "1.0")
}

private final class SoftwareUpdateCommandFixture: @unchecked Sendable {
    struct Call {
        let path: String
        let arguments: [String]
        let environment: [String: String]
    }
    private let lock = NSLock()
    private var replies: [SoftwareUpdateCommandOutput]
    private(set) var calls: [Call] = []

    init(_ replies: [SoftwareUpdateCommandOutput]) { self.replies = replies }

    func run(_ path: String, _ arguments: [String], _ timeout: TimeInterval, _ environment: [String: String]) -> SoftwareUpdateCommandOutput {
        lock.lock(); defer { lock.unlock() }
        calls.append(Call(path: path, arguments: arguments, environment: environment))
        guard !replies.isEmpty else { return .init(status: 99, stderr: "Unexpected fixture command") }
        return replies.removeFirst()
    }

    var service: HomebrewSoftwareUpdateService {
        HomebrewSoftwareUpdateService(brewPath: "/fixture/bin/brew", runner: { [self] in run($0, $1, $2, $3) },
                                      evidenceReader: { _, _, _ in .init() })
    }
}

final class SoftwareUpdateParserTests: XCTestCase {
    func testInstalledReceiptAndActualAppVersionStayDistinct() throws {
        let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(appVersion: "1.5"),
                                                        outdatedJSON: SoftwareUpdateFixtures.outdated())
        let item = try XCTUnwrap(values.first)
        XCTAssertEqual(item.caskID, "homebrew/cask/fixture")
        XCTAssertEqual(item.recordedVersion, "1.0")
        XCTAssertEqual(item.installedVersion, "1.5")
        XCTAssertEqual(item.availableVersion, "2.0")
        XCTAssertEqual(item.provider, "Homebrew")
    }

    func testUninstalledOutdatedRecordCannotBecomeAnInstallCandidate() throws {
        let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(installed: nil),
                                                        outdatedJSON: SoftwareUpdateFixtures.outdated())
        XCTAssertTrue(values.isEmpty)
    }

    func testAmbiguousPackageAcrossTapsFailsClosed() {
        let input = SoftwareUpdateFixtures.json([SoftwareUpdateFixtures.cask(tap: "one/apps"), SoftwareUpdateFixtures.cask(tap: "two/apps")])
        XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: input, outdatedJSON: SoftwareUpdateFixtures.outdated()))
    }

    func testFullyQualifiedOutdatedIdentitySelectsTheCorrectInstalledTap() throws {
        let input = SoftwareUpdateFixtures.json([SoftwareUpdateFixtures.cask(tap: "one/apps"), SoftwareUpdateFixtures.cask(tap: "two/apps")])
        let values = try HomebrewUpdateParser.candidates(inventoryJSON: input, outdatedJSON: SoftwareUpdateFixtures.outdated(name: "two/apps/fixture"))
        XCTAssertEqual(values.map(\.caskID), ["two/apps/fixture"])
    }

    func testInconsistentTapAndFullTokenAreRejected() {
        var row = SoftwareUpdateFixtures.cask(tap: "one/apps")
        row["full_token"] = "two/apps/fixture"
        XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.json([row]), outdatedJSON: SoftwareUpdateFixtures.outdated()))
    }

    func testDuplicateInstalledIdentityIsNotSilentlyMerged() {
        let input = SoftwareUpdateFixtures.json([SoftwareUpdateFixtures.cask(), SoftwareUpdateFixtures.cask()])
        XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: input, outdatedJSON: SoftwareUpdateFixtures.outdated()))
    }

    func testChangingCatalogueIsAnErrorNotAnInventedTarget() {
        XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(),
                                                                outdatedJSON: SoftwareUpdateFixtures.outdated(current: "3.0")))
    }

    func testShellAndOptionLikePackageNamesAreRejected() {
        for value in ["--force", "fixture;rm", "$(touch-file)", "../fixture", "a/b", "a//fixture", "https://host/cask", "fixture\n--force"] {
            XCTAssertFalse(HomebrewUpdateParser.isValidCaskID(value), value)
        }
        for value in ["firefox", "homebrew/cask/visual-studio-code", "vendor/apps/rider@2025.1"] {
            XCTAssertTrue(HomebrewUpdateParser.isValidCaskID(value), value)
        }
    }

    func testMissingOrInvalidJSONCannotLookLikeASuccessfulEmptyCheck() {
        for value in ["not JSON", "{}", #"{"casks":null}"#, #"{"casks":[{"token":"fixture"}]}"#] {
            XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: value, outdatedJSON: SoftwareUpdateFixtures.outdated()))
        }
        XCTAssertThrowsError(try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(), outdatedJSON: #"{"casks":[{"name":"fixture"}]}"#))
    }

    func testOlderHomebrewWithoutBundleMetadataLabelsInstalledReceipt() throws {
        var row = SoftwareUpdateFixtures.cask(appVersion: nil)
        row.removeValue(forKey: "bundle_short_version")
        row.removeValue(forKey: "bundle_version")
        let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.json([row]), outdatedJSON: SoftwareUpdateFixtures.outdated())
        XCTAssertEqual(values.first?.installedVersion, "1.0")
        XCTAssertNil(values.first?.appVersion)
    }

    func testUntrustedHomepageSchemesAreNotExposed() throws {
        for value in ["file:///tmp/app", "javascript:alert(1)", "https://user:password@example.com/", "http://example.com"] {
            var row = SoftwareUpdateFixtures.cask()
            row["homepage"] = value
            let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.json([row]), outdatedJSON: SoftwareUpdateFixtures.outdated())
            XCTAssertNil(values.first?.homepage)
        }
    }

    func testPinnedFlagFromOutdatedDataPreventsSelection() throws {
        let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(), outdatedJSON: SoftwareUpdateFixtures.outdated(pinned: true))
        XCTAssertEqual(values.first?.blockedReason, "Pinned in Homebrew")
    }

    func testSelfUpdatedAppIsNotOfferedAReinstallOrDowngrade() throws {
        for appVersion in ["2.0", "3.0"] {
            let values = try HomebrewUpdateParser.candidates(inventoryJSON: SoftwareUpdateFixtures.inventory(appVersion: appVersion), outdatedJSON: SoftwareUpdateFixtures.outdated())
            XCTAssertNotNil(values.first?.blockedReason)
        }
    }

    func testReceiptCannotOfferANumericDowngrade() {
        let item = SoftwareUpdateCandidate(caskID: "homebrew/cask/fixture", name: "Fixture", recordedVersion: "12.2", availableVersion: "12.1")
        XCTAssertNotNil(item.blockedReason)
    }

    func testBuildVersionComparisonPreservesCaskBuildDistinction() {
        XCTAssertTrue(SoftwareUpdateVersion.matchesApp(shortVersion: "1.2", build: "500", target: "1.2,500"))
        XCTAssertFalse(SoftwareUpdateVersion.matchesApp(shortVersion: "1.2", build: nil, target: "1.2,500"))
        XCTAssertFalse(SoftwareUpdateVersion.matchesApp(shortVersion: "1.2", build: "600", target: "1.2,500"))
        let item = SoftwareUpdateCandidate(caskID: "homebrew/cask/fixture", name: "Fixture", recordedVersion: "1.1,400",
                                          availableVersion: "1.2,500", appVersion: "1.2", appBuild: "600")
        XCTAssertNotNil(item.blockedReason)
    }

    func testVersionComparisonUsesNumericComponentsAndDoesNotGuessVendorOrdering() {
        XCTAssertTrue(SoftwareUpdateVersion.isClearlyNewer("10.2", than: "2.99"))
        XCTAssertFalse(SoftwareUpdateVersion.isClearlyNewer("1.0.0", than: "1.0"))
        XCTAssertFalse(SoftwareUpdateVersion.isClearlyNewer("2026-R2", than: "2026-R1"))
        XCTAssertFalse(SoftwareUpdateVersion.isClearlyNewer("latest", than: "1.0"))
        XCTAssertFalse(SoftwareUpdateVersion.isClearlyNewer("999999999999999999999999999999999", than: "1.0"))
    }
}

final class SoftwareUpdateServiceTests: XCTestCase {
    func testCheckRefreshesCatalogueThenUsesInstalledInventoryAndOutdatedJSON() throws {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 0), .init(status: 0, stdout: SoftwareUpdateFixtures.inventory()), .init(status: 0, stdout: SoftwareUpdateFixtures.outdated())])
        let scan = try fixture.service.checkForUpdates()
        XCTAssertEqual(scan.managedCaskCount, 1)
        XCTAssertEqual(scan.candidates.count, 1)
        XCTAssertEqual(fixture.calls.map(\.arguments), [
            ["update", "--quiet"], ["info", "--json=v2", "--cask", "--installed"], ["outdated", "--cask", "--json=v2"]
        ])
    }

    func testCatalogueFailureCannotShowCachedVersionsAsCurrent() {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 1, stderr: "Network unavailable")])
        XCTAssertThrowsError(try fixture.service.checkForUpdates())
        XCTAssertEqual(fixture.calls.count, 1)
    }

    func testVerifiedUpdateRereadsRealInstalledVersionAndUsesOnlyApprovedCask() {
        let fixture = successfulUpgrade(after: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: "2.0"))
        let result = fixture.service.update(SoftwareUpdateFixtures.candidate)
        XCTAssertEqual(result.status, .updated)
        XCTAssertEqual(result.recordedVersionAfter, "2.0")
        XCTAssertEqual(result.appVersionAfter, "2.0")
        XCTAssertEqual(fixture.calls.map(\.arguments), [
            ["info", "--json=v2", "--cask", "--installed"], ["outdated", "--cask", "--json=v2"],
            ["upgrade", "--cask", "homebrew/cask/fixture"], ["info", "--json=v2", "--cask", "--installed"]
        ])
        XCTAssertEqual(fixture.calls[2].environment["HOMEBREW_NO_AUTO_UPDATE"], "1")
        XCTAssertEqual(fixture.calls[2].environment["HOMEBREW_NO_INSTALL_CLEANUP"], "1")
        XCTAssertTrue(fixture.calls.allSatisfy { $0.path == "/fixture/bin/brew" && !$0.arguments.contains("--force") && !$0.arguments.contains("--no-quarantine") })
    }

    func testExitZeroWithoutInstalledVersionChangeIsUnverified() {
        let fixture = successfulUpgrade(after: SoftwareUpdateFixtures.inventory())
        let result = fixture.service.update(SoftwareUpdateFixtures.candidate)
        XCTAssertEqual(result.status, .unverified)
        XCTAssertEqual(result.recordedVersionAfter, "1.0")
    }

    func testUpdatedReceiptWithWrongAppVersionIsUnverified() {
        let fixture = successfulUpgrade(after: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: "1.8"))
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .unverified)
    }

    func testDisappearingAppVersionIsNotReportedAsVerified() {
        let fixture = successfulUpgrade(after: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: nil))
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .unverified)
    }

    func testFailedInstallerDoesNotBecomeSuccessfulJustBecauseReceiptChanged() {
        let fixture = successfulUpgrade(after: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: "2.0"), exitStatus: 1)
        let result = fixture.service.update(SoftwareUpdateFixtures.candidate)
        XCTAssertEqual(result.status, .failed)
        XCTAssertEqual(result.recordedVersionAfter, "2.0")
        XCTAssertEqual(result.exitStatus, 1)
    }

    func testPostInstallReadFailureIsUnverified() {
        let fixture = SoftwareUpdateCommandFixture([
            .init(status: 0, stdout: SoftwareUpdateFixtures.inventory()), .init(status: 0, stdout: SoftwareUpdateFixtures.outdated()),
            .init(status: 0), .init(status: 1, stderr: "Could not read metadata")
        ])
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .unverified)
    }

    func testChangedTargetRequiresFreshReviewAndDoesNotRunUpgrade() {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 0, stdout: SoftwareUpdateFixtures.inventory(available: "3.0"))])
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .blocked)
        XCTAssertEqual(fixture.calls.count, 1)
    }

    func testChangedInstalledAppRequiresFreshReview() {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 0, stdout: SoftwareUpdateFixtures.inventory(appVersion: "1.8"))])
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .blocked)
        XCTAssertEqual(fixture.calls.count, 1)
    }

    func testUninstalledOrPinnedPackageDoesNotRunUpgrade() {
        for input in [SoftwareUpdateFixtures.inventory(installed: nil), SoftwareUpdateFixtures.inventory(pinned: true)] {
            let fixture = SoftwareUpdateCommandFixture([.init(status: 0, stdout: input)])
            XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .blocked)
            XCTAssertEqual(fixture.calls.count, 1)
        }
    }

    func testNoLongerOutdatedDoesNotReinstall() {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 0, stdout: SoftwareUpdateFixtures.inventory()), .init(status: 0, stdout: SoftwareUpdateFixtures.json([]))])
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .blocked)
        XCTAssertEqual(fixture.calls.count, 2)
    }

    func testAlreadyCurrentIsRecordedWithoutAnInstallerInvocation() {
        let fixture = SoftwareUpdateCommandFixture([.init(status: 0, stdout: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: "2.0"))])
        XCTAssertEqual(fixture.service.update(SoftwareUpdateFixtures.candidate).status, .alreadyCurrent)
        XCTAssertEqual(fixture.calls.count, 1)
    }

    func testInvalidPackageIdentityDoesNotRunAnyCommand() {
        let fixture = SoftwareUpdateCommandFixture([])
        let candidate = SoftwareUpdateCandidate(caskID: "--force", name: "Invalid", recordedVersion: "1.0", availableVersion: "2.0")
        XCTAssertEqual(fixture.service.update(candidate).status, .blocked)
        XCTAssertTrue(fixture.calls.isEmpty)
    }

    func testMissingHomebrewProducesAnActionableProviderError() {
        let service = HomebrewSoftwareUpdateService(brewPath: nil)
        XCTAssertThrowsError(try service.checkForUpdates())
        XCTAssertEqual(service.update(SoftwareUpdateFixtures.candidate).status, .blocked)
    }

    func testCaskWithoutAnyAppMetadataVerifiesOnlyProviderRecord() {
        let fixture = SoftwareUpdateCommandFixture([
            .init(status: 0, stdout: SoftwareUpdateFixtures.inventory(appVersion: nil)), .init(status: 0, stdout: SoftwareUpdateFixtures.outdated()),
            .init(status: 0), .init(status: 0, stdout: SoftwareUpdateFixtures.inventory(installed: "2.0", appVersion: nil))
        ])
        let candidate = SoftwareUpdateCandidate(caskID: "homebrew/cask/fixture", name: "Fixture", recordedVersion: "1.0", availableVersion: "2.0")
        let result = fixture.service.update(candidate)
        XCTAssertEqual(result.status, .updated)
        XCTAssertTrue(result.details.contains("installation record"))
        XCTAssertNil(result.appVersionAfter)
    }

    private func successfulUpgrade(after: String, exitStatus: Int32 = 0) -> SoftwareUpdateCommandFixture {
        SoftwareUpdateCommandFixture([
            .init(status: 0, stdout: SoftwareUpdateFixtures.inventory()), .init(status: 0, stdout: SoftwareUpdateFixtures.outdated()),
            .init(status: exitStatus, stdout: "Fixture installer output"), .init(status: 0, stdout: after)
        ])
    }
}
