import XCTest
@testable import EvictKit

final class MacReleaseVersionTests: XCTestCase {
    func testCompactAndLegacyBetaOrdering() throws {
        let stable = try XCTUnwrap(MacReleaseVersion("2.1.0"))
        let beta = try XCTUnwrap(MacReleaseVersion("2.1.0-beta012"))
        XCTAssertGreaterThan(stable, beta)
        XCTAssertGreaterThan(beta, try XCTUnwrap(MacReleaseVersion("2.1.0-beta011")))
        XCTAssertGreaterThan(beta, try XCTUnwrap(MacReleaseVersion("1.999.999")))
        XCTAssertEqual(MacReleaseVersion("2.1.0-beta001"), MacReleaseVersion("2.1.0-beta.1"))
        XCTAssertGreaterThan(try XCTUnwrap(MacReleaseVersion("1.0.0-beta.200.3.2")),
                             try XCTUnwrap(MacReleaseVersion("1.0.0-beta.200.3.1")))
        XCTAssertEqual(beta.core, "2.1.0")
        XCTAssertEqual(beta.rawValue, "2.1.0-beta012")
    }

    func testInvalidAndUnboundedVersionsAreRejected() {
        for value in ["", "1.0", "v1.0.0", "01.0.0", "1.0.0-beta12", "1.0.0-beta000", "1.0.0-beta1234", "1.0.0-rc1",
                      "-1.0.0", "1.0.0+anything", "1.0.0\n", "99999999999999999999999.0.0", "1.0.0-beta.99999999999999999999999"] {
            XCTAssertNil(MacReleaseVersion(value), value)
        }
    }

    func testVersionRoundTripRetainsExactReleaseIdentity() throws {
        let version = try XCTUnwrap(MacReleaseVersion("3.0.0-beta007"))
        let data = try JSONEncoder().encode(version)
        XCTAssertEqual(try JSONDecoder().decode(MacReleaseVersion.self, from: data).rawValue, version.rawValue)
        XCTAssertThrowsError(try JSONDecoder().decode(MacReleaseVersion.self, from: Data(#""not-a-version""#.utf8)))
    }
}

final class MacReleaseCatalogTests: XCTestCase {
    func testNativeCatalogRejectsWindowsDraftAndMismatchedChannels() throws {
        var windows = releaseJSON("4.0.0")
        windows["tag_name"] = "win-v4.0.0"
        var draft = releaseJSON("3.0.0")
        draft["draft"] = true
        var incorrectChannel = releaseJSON("2.0.0-beta001")
        incorrectChannel["prerelease"] = false
        let data = try JSONSerialization.data(withJSONObject: [windows, draft, incorrectChannel, releaseJSON("1.1.0"), releaseJSON("1.2.0-beta001")])
        let catalog = try MacReleaseCatalog.decode(data)
        XCTAssertEqual(catalog.map(\.version.rawValue), ["1.2.0-beta001", "1.1.0"])
    }

    func testOnlyMatchingNativeUniversalOrLegacyArchiveIsAccepted() throws {
        var wrongArchitecture = releaseJSON("1.2.0", archiveName: "Evict-1.2.0-macOS-arm64.zip")
        var missingChecksum = releaseJSON("1.3.0")
        missingChecksum["assets"] = Array((missingChecksum["assets"] as! [[String: Any]]).prefix(1))
        var wrongVersion = releaseJSON("1.4.0", archiveName: "Evict-4.1.2-macOS-universal.zip")
        var wrongHost = releaseJSON("1.5.0")
        var assets = wrongHost["assets"] as! [[String: Any]]
        assets[0]["browser_download_url"] = "https://example.com/Evict.zip"
        wrongHost["assets"] = assets
        let tooLarge = releaseJSON("1.6.0", size: MacReleasePolicy.maximumArchiveBytes + 1)
        // Keep local mutations explicit: all of these must be rejected, not merely sorted later.
        wrongArchitecture["name"] = "Apple Silicon only"
        wrongVersion["name"] = "A Windows version in a Mac release"
        let legacy = releaseJSON("1.0.1", archiveName: "Evict-1.0.1.zip")
        let data = try JSONSerialization.data(withJSONObject: [wrongArchitecture, missingChecksum, wrongVersion, wrongHost, tooLarge, legacy])
        let catalog = try MacReleaseCatalog.decode(data)
        XCTAssertEqual(catalog.map(\.version.rawValue), ["1.0.1"])
    }

    func testLatestPerChannelDoesNotHideHistoricalSelectionWhenRequested() throws {
        let releases = try catalog(["1.0.0", "1.1.0", "1.2.0-beta001", "1.2.0-beta002"])
        XCTAssertEqual(MacReleasePolicy.visible(releases, includeBeta: false, showAll: false).map(\.version.rawValue), ["1.1.0"])
        XCTAssertEqual(MacReleasePolicy.visible(releases, includeBeta: true, showAll: false).map(\.version.rawValue), ["1.2.0-beta002", "1.1.0"])
        XCTAssertEqual(MacReleasePolicy.visible(releases, includeBeta: true, showAll: true).count, 4)
        XCTAssertEqual(MacReleasePolicy.visible(releases, includeBeta: false, showAll: true).count, 2)
    }

    func testNotificationRespectsPinnedSkippedAndBetaPreferences() throws {
        let releases = try catalog(["1.0.0", "1.1.0", "1.2.0-beta001"])
        var preferences = MacReleasePreferences()
        XCTAssertEqual(MacReleasePolicy.availableUpdate(releases, current: "1.0.0", preferences: preferences)?.version.rawValue, "1.1.0")
        preferences.skippedTags = ["mac-v1.1.0"]
        XCTAssertNil(MacReleasePolicy.availableUpdate(releases, current: "1.0.0", preferences: preferences))
        preferences.includeBeta = true
        XCTAssertEqual(MacReleasePolicy.availableUpdate(releases, current: "1.0.0", preferences: preferences)?.version.rawValue, "1.2.0-beta001")
        preferences.pinnedVersion = "1.0.0"
        XCTAssertNil(MacReleasePolicy.availableUpdate(releases, current: "1.0.0", preferences: preferences))
        preferences.pinnedVersion = nil
        XCTAssertNil(MacReleasePolicy.availableUpdate(releases, current: "9.0.0", preferences: preferences))
        XCTAssertNil(MacReleasePolicy.availableUpdate(releases, current: "unknown", preferences: preferences))
    }

    func testURLsRequireExactRepositoryVersionAssetAndHTTPS() throws {
        let tag = "mac-v1.0.0-beta001", name = "Evict-1.0.0-beta001-macOS-universal.zip"
        let good = "https://github.com/krishnabhunia/evict-uninstaller/releases/download/\(tag)/\(name)"
        XCTAssertTrue(MacReleasePolicy.isReleaseAssetURL(try XCTUnwrap(URL(string: good)), tag: tag, assetName: name))
        for bad in [good.replacingOccurrences(of: "https:", with: "http:"),
                    good.replacingOccurrences(of: "github.com", with: "github.com.evil.example"),
                    good.replacingOccurrences(of: "github.com", with: "user:pass@github.com"),
                    good.replacingOccurrences(of: "krishnabhunia", with: "another-user"),
                    good.replacingOccurrences(of: "mac-v", with: "win-v"), good + "?raw=true", good + "#fragment",
                    good.replacingOccurrences(of: "github.com/", with: "github.com:443/")] {
            XCTAssertFalse(MacReleasePolicy.isReleaseAssetURL(try XCTUnwrap(URL(string: bad)), tag: tag, assetName: name), bad)
        }
        let initial = try XCTUnwrap(URL(string: good))
        XCTAssertTrue(MacReleasePolicy.permitsRedirect(URL(string: "https://release-assets.githubusercontent.com/github-production-release-asset/123?signature=abc")!, initialURL: initial))
        XCTAssertFalse(MacReleasePolicy.permitsRedirect(URL(string: "https://evil.example/payload")!, initialURL: initial))
        XCTAssertFalse(MacReleasePolicy.permitsRedirect(URL(string: "http://objects.githubusercontent.com/payload")!, initialURL: initial))
    }

    private func catalog(_ versions: [String]) throws -> [MacRelease] {
        try MacReleaseCatalog.decode(JSONSerialization.data(withJSONObject: versions.map { releaseJSON($0) }))
    }

    private func releaseJSON(_ version: String, archiveName: String? = nil, size: Int64 = 100) -> [String: Any] {
        let tag = "mac-v\(version)", name = archiveName ?? "Evict-\(version)-macOS-universal.zip"
        let base = "https://github.com/krishnabhunia/evict-uninstaller/releases/download/\(tag)/"
        return ["tag_name": tag, "name": "Evict \(version)", "body": "- Native application update and recovery improvements.",
                "draft": false, "prerelease": version.contains("-beta"), "published_at": "2026-11-07T10:00:00Z",
                "assets": [["name": name, "browser_download_url": base + name, "size": size],
                           ["name": name + ".sha256", "browser_download_url": base + name + ".sha256", "size": 100]]]
    }
}

final class MacReleaseIntegrityTests: XCTestCase {
    private let digest = String(repeating: "ab", count: 32)

    func testChecksumSelectsExactlyTheNamedAsset() throws {
        let manifest = "\(String(repeating: "1", count: 64))  another.zip\n\(digest.uppercased()) *Evict.zip\n"
        XCTAssertEqual(try MacReleaseIntegrity.expectedDigest(Data(manifest.utf8), assetName: "Evict.zip"), digest)
        XCTAssertThrowsError(try MacReleaseIntegrity.expectedDigest(Data(manifest.utf8), assetName: "missing.zip"))
        XCTAssertThrowsError(try MacReleaseIntegrity.expectedDigest(Data("\(digest) Evict.zip\n\(digest) Evict.zip".utf8), assetName: "Evict.zip"))
        XCTAssertThrowsError(try MacReleaseIntegrity.expectedDigest(Data("\(digest) ../../Evict.zip".utf8), assetName: "Evict.zip"))
        XCTAssertThrowsError(try MacReleaseIntegrity.expectedDigest(Data("1234 Evict.zip".utf8), assetName: "Evict.zip"))
    }

    func testSHA256KnownVectorAndRegularFileGuard() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let file = directory.appendingPathComponent("abc")
        try Data("abc".utf8).write(to: file)
        XCTAssertEqual(try MacReleaseIntegrity.sha256(of: file), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
        let link = directory.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: file)
        XCTAssertThrowsError(try MacReleaseIntegrity.sha256(of: link))
        XCTAssertThrowsError(try MacReleaseIntegrity.sha256(of: directory))
    }

    func testArchiveAllowsOnlyTheNativeAppAndChecksBothHeaders() throws {
        let archive = zip(entries: standardEntries)
        XCTAssertEqual(try MacReleaseArchive.inspect(archive).paths, standardEntries.map(\.name))
        var inconsistent = standardEntries
        inconsistent[0].localName = "Evict.app/Contents/Other.plist"
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: inconsistent)))
        XCTAssertThrowsError(try MacReleaseArchive.inspect(Data(archive.dropLast(4))))
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: [standardEntries[0]])))
    }

    func testArchiveRejectsTraversalLinksPrivilegeAndDuplicatePaths() throws {
        for path in ["../outside", "/tmp/outside", "Evict.app/../outside", "Evict.app//outside", "Evict.app/./outside",
                     "Evict.app/Contents/evil\\path", "Another.app/Contents/Info.plist", "Evict.app/Contents/evil\npath"] {
            XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: standardEntries + [Entry(name: path)])), path)
        }
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: standardEntries + [Entry(name: "Evict.app/link", mode: 0xa1ff)])))
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: standardEntries + [Entry(name: "Evict.app/suid", mode: 0x89ed)])))
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: standardEntries + [Entry(name: "evict.app/contents/info.plist")])))
        var encrypted = standardEntries
        encrypted[0].flags = 1
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: encrypted)))
        var bomb = standardEntries
        bomb[0].expandedBytes = UInt32(MacReleasePolicy.maximumArchiveBytes + 1)
        XCTAssertThrowsError(try MacReleaseArchive.inspect(zip(entries: bomb)))
    }

    private struct Entry {
        let name: String
        var localName: String?
        var mode: UInt32 = 0x81ed
        var flags: UInt16 = 0
        var expandedBytes: UInt32 = 3
    }
    private var standardEntries: [Entry] {
        [Entry(name: "Evict.app/Contents/Info.plist"), Entry(name: "Evict.app/Contents/MacOS/Evict")]
    }

    private func zip(entries: [Entry]) -> Data {
        func u16(_ value: UInt16) -> Data { Data([UInt8(truncatingIfNeeded: value), UInt8(truncatingIfNeeded: value >> 8)]) }
        func u32(_ value: UInt32) -> Data { u16(UInt16(truncatingIfNeeded: value)) + u16(UInt16(truncatingIfNeeded: value >> 16)) }
        var locals = Data(), directory = Data()
        for entry in entries {
            let name = Data(entry.name.utf8), localName = Data((entry.localName ?? entry.name).utf8)
            let offset = UInt32(locals.count)
            locals += u32(0x04034b50) + u16(20) + u16(entry.flags) + u16(0) + u16(0) + u16(0)
            locals += u32(0) + u32(3) + u32(entry.expandedBytes) + u16(UInt16(localName.count)) + u16(0)
            locals += localName + Data([1, 2, 3])
            directory += u32(0x02014b50) + u16(0x0314) + u16(20) + u16(entry.flags) + u16(0) + u16(0) + u16(0)
            directory += u32(0) + u32(3) + u32(entry.expandedBytes) + u16(UInt16(name.count)) + u16(0) + u16(0)
            directory += u16(0) + u16(0) + u32(entry.mode << 16) + u32(offset) + name
        }
        var end = u32(0x06054b50) + u16(0) + u16(0) + u16(UInt16(entries.count)) + u16(UInt16(entries.count))
        end += u32(UInt32(directory.count)) + u32(UInt32(locals.count)) + u16(0)
        return locals + directory + end
    }
}

final class MacReleaseStoreTests: XCTestCase {
    private var directory: URL!
    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws { try FileManager.default.removeItem(at: directory) }

    func testPinSkipAndHistorySurviveRestart() throws {
        let store = MacReleaseStore(directory: directory)
        let entry = historyEntry()
        try store.update {
            $0.preferences.includeBeta = true
            $0.preferences.pinnedVersion = "1.0.0"
            $0.preferences.skippedTags = ["mac-v1.1.0", "mac-v1.1.0"]
            $0.history = [entry]
        }
        let restarted = MacReleaseStore(directory: directory)
        XCTAssertNil(restarted.lastError)
        XCTAssertTrue(restarted.snapshot.preferences.includeBeta)
        XCTAssertEqual(restarted.snapshot.preferences.pinnedVersion, "1.0.0")
        XCTAssertEqual(restarted.snapshot.preferences.skippedTags, ["mac-v1.1.0"])
        XCTAssertEqual(restarted.snapshot.history.first?.id, entry.id)
        XCTAssertEqual(restarted.snapshot.history.first?.status, .prepared)
    }

    func testOlderSettingsDefaultNewFieldsWithoutDiscardingExistingValues() throws {
        try Data(#"{"preferences":{"includeBeta":true}}"#.utf8).write(to: directory.appendingPathComponent("state.json"))
        let store = MacReleaseStore(directory: directory)
        XCTAssertTrue(store.snapshot.preferences.includeBeta)
        XCTAssertFalse(store.snapshot.preferences.showAllBuilds)
        XCTAssertTrue(store.snapshot.history.isEmpty)
        XCTAssertTrue(store.snapshot.recoveryCopies.isEmpty)
    }

    func testCorruptStateIsPreservedAndMutationsFailClearly() throws {
        let corrupt = Data("{broken history".utf8), file = directory.appendingPathComponent("state.json")
        try corrupt.write(to: file)
        let store = MacReleaseStore(directory: directory)
        XCTAssertNotNil(store.lastError)
        XCTAssertThrowsError(try store.update { $0.preferences.includeBeta = true })
        XCTAssertEqual(try Data(contentsOf: file), corrupt)
        XCTAssertFalse(store.snapshot.preferences.includeBeta)
    }

    func testInstallationIsOnlyVerifiedAtExpectedPathWithExactVersionAndDigest() throws {
        let store = MacReleaseStore(directory: directory), entry = historyEntry()
        try store.update { $0.history = [entry] }
        XCTAssertFalse(try store.reconcile(version: "1.1.0", bundlePath: "/tmp/Staging/Evict.app", executableDigest: "abc"))
        XCTAssertFalse(try store.reconcile(version: "1.0.0", bundlePath: entry.installationPath, executableDigest: "abc"))
        XCTAssertFalse(try store.reconcile(version: "1.1.0", bundlePath: entry.installationPath, executableDigest: "changed"))
        XCTAssertEqual(store.snapshot.history[0].status, .prepared)
        XCTAssertTrue(try store.reconcile(version: "1.1.0", bundlePath: entry.installationPath, executableDigest: "abc"))
        XCTAssertEqual(store.snapshot.history[0].status, .verified)
        XCTAssertNotNil(store.snapshot.history[0].completedAt)
    }

    func testStateSymlinkCannotOverwriteAnotherFile() throws {
        let target = directory.appendingPathComponent("original"), file = directory.appendingPathComponent("state.json")
        let original = Data("valuable".utf8)
        try original.write(to: target)
        let store = MacReleaseStore(directory: directory)
        try FileManager.default.createSymbolicLink(at: file, withDestinationURL: target)
        XCTAssertThrowsError(try store.update { $0.preferences.includeBeta = true })
        XCTAssertEqual(try Data(contentsOf: target), original)
    }

    private func historyEntry() -> MacReleaseHistoryEntry {
        MacReleaseHistoryEntry(operation: .install, fromVersion: "1.0.0", toVersion: "1.1.0", releaseTag: "mac-v1.1.0",
                               detail: "Prepared", installationPath: "/Applications/Evict.app", executableDigest: "abc")
    }
}

final class MacReleaseBundleMetadataTests: XCTestCase {
    func testNumericBundleVersionAndExactBetaIdentityAreDistinct() throws {
        let data = try plist(version: "1.2.0", release: "1.2.0-beta007")
        let metadata = try MacReleaseBundleMetadata.read(data, expectedVersion: "1.2.0-beta007")
        XCTAssertEqual(metadata.version.rawValue, "1.2.0-beta007")
        XCTAssertEqual(metadata.version.core, "1.2.0")
        XCTAssertThrowsError(try MacReleaseBundleMetadata.read(data, expectedVersion: "1.2.0-beta008"))
        XCTAssertThrowsError(try MacReleaseBundleMetadata.read(data, expectedVersion: "1.2.0"))
    }

    func testLegacyNativeBundleIdentityAndInvalidMetadata() throws {
        XCTAssertEqual(try MacReleaseBundleMetadata.read(plist(version: "1.0.1"), expectedVersion: "1.0.1").version.rawValue, "1.0.1")
        XCTAssertThrowsError(try MacReleaseBundleMetadata.read(plist(version: "9.0.0", release: "1.0.0"), expectedVersion: "1.0.0"))
        XCTAssertThrowsError(try MacReleaseBundleMetadata.read(plist(version: "1.0.0", identifier: "unrelated.app"), expectedVersion: "1.0.0"))
        XCTAssertThrowsError(try MacReleaseBundleMetadata.read(plist(version: "1.0.0", minimum: "unknown"), expectedVersion: "1.0.0"))
    }

    private func plist(version: String, release: String? = nil, identifier: String = "app.evict.mac", minimum: String = "14.0") throws -> Data {
        var values: [String: String] = ["CFBundleIdentifier": identifier, "CFBundleExecutable": "Evict", "CFBundlePackageType": "APPL",
                                        "CFBundleShortVersionString": version, "CFBundleVersion": "7", "LSMinimumSystemVersion": minimum]
        values["EvictReleaseVersion"] = release
        return try PropertyListSerialization.data(fromPropertyList: values, format: .xml, options: 0)
    }
}

#if os(macOS)
final class MacReleasePreparationTests: XCTestCase {
    func testNativePreparationMakesVerifiedRecoveryWithoutReplacingCurrentApp() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("Evict-native-release-test-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("main.c"), binary = root.appendingPathComponent("Evict")
        try Data("int main(void) { return 0; }\n".utf8).write(to: source)
        let compile = Shell.run("/usr/bin/xcrun", ["clang", "-arch", "arm64", "-arch", "x86_64", "-mmacosx-version-min=14.0", source.path, "-o", binary.path], timeout: 90)
        XCTAssertTrue(compile.ok, compile.stderr)
        guard compile.ok else { return }
        let current = try makeApp(root.appendingPathComponent("Current/Evict.app"), version: "1.0.0", binary: binary)
        let target = try makeApp(root.appendingPathComponent("Target/Evict.app"), version: "1.1.0-beta001", binary: binary)
        let archive = root.appendingPathComponent("Evict-1.1.0-beta001-macOS-universal.zip")
        let zip = Shell.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", target.path, archive.path])
        XCTAssertTrue(zip.ok, zip.stderr)
        let size = (try FileManager.default.attributesOfItem(atPath: archive.path)[.size] as! NSNumber).int64Value
        let version = MacReleaseVersion("1.1.0-beta001")!
        let assetURL = URL(string: "https://github.com/krishnabhunia/evict-uninstaller/releases/download/mac-v1.1.0-beta001/\(archive.lastPathComponent)")!
        let release = MacRelease(tag: "mac-v1.1.0-beta001", version: version, title: "Native fixture", notes: "Test release",
                                 archive: MacReleaseAsset(name: archive.lastPathComponent, url: assetURL, size: size),
                                 checksum: MacReleaseAsset(name: archive.lastPathComponent + ".sha256", url: URL(string: assetURL.absoluteString + ".sha256")!, size: 120))
        let store = MacReleaseStore(directory: root.appendingPathComponent("Version Manager"))
        let before = try MacReleaseIntegrity.sha256(of: current.appendingPathComponent("Contents/MacOS/Evict"))
        let ready = try MacReleasePreparation.prepare(archive: archive, release: release, currentBundle: current, currentVersion: "1.0.0", store: store)
        XCTAssertEqual(store.snapshot.history.first?.status, .prepared)
        XCTAssertEqual(store.snapshot.recoveryCopies.count, 1)
        XCTAssertEqual(try MacReleaseIntegrity.sha256(of: current.appendingPathComponent("Contents/MacOS/Evict")), before)
        XCTAssertEqual(try MacReleasePreparation.validatePrepared(ready.entry, store: store).applicationURL, ready.applicationURL)
        let copy = try XCTUnwrap(store.snapshot.recoveryCopies.first)
        let restored = try MacReleasePreparation.prepareRollback(copy, currentBundle: current, currentVersion: "1.0.0", store: store)
        XCTAssertEqual(restored.entry.toVersion, "1.0.0")
        XCTAssertEqual(restored.entry.operation, .rollback)
        try Data("tampered executable".utf8).write(to: restored.applicationURL.appendingPathComponent("Contents/MacOS/Evict"))
        XCTAssertThrowsError(try MacReleasePreparation.validatePrepared(restored.entry, store: store))
        try Data("tampered recovery archive".utf8).write(to: store.recoveryArchive(for: copy.id))
        XCTAssertThrowsError(try MacReleasePreparation.prepareRollback(copy, currentBundle: current, currentVersion: "1.0.0", store: store))
        XCTAssertEqual(try MacReleaseIntegrity.sha256(of: current.appendingPathComponent("Contents/MacOS/Evict")), before)
    }

    private func makeApp(_ application: URL, version: String, binary: URL) throws -> URL {
        let manager = FileManager.default
        try manager.createDirectory(at: application.appendingPathComponent("Contents/MacOS"), withIntermediateDirectories: true)
        try manager.copyItem(at: binary, to: application.appendingPathComponent("Contents/MacOS/Evict"))
        let values: [String: String] = ["CFBundleIdentifier": "app.evict.mac", "CFBundleExecutable": "Evict", "CFBundlePackageType": "APPL",
                                        "CFBundleShortVersionString": MacReleaseVersion(version)!.core, "EvictReleaseVersion": version,
                                        "CFBundleVersion": "7", "LSMinimumSystemVersion": "14.0"]
        try PropertyListSerialization.data(fromPropertyList: values, format: .xml, options: 0).write(to: application.appendingPathComponent("Contents/Info.plist"))
        let sign = Shell.run("/usr/bin/codesign", ["--force", "--sign", "-", application.path])
        guard sign.ok else { throw MacReleaseError.file("Could not sign native test fixture: \(sign.stderr)") }
        return application
    }
}
#endif
