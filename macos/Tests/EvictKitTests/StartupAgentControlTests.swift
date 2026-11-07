import Foundation
import XCTest
@testable import EvictKit
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

final class StartupAgentControlTests: XCTestCase {
    private final class Launchd: @unchecked Sendable {
        let lock = NSLock()
        let path: String
        let label: String
        var loaded: Bool
        var disabled: Bool?
        var calls: [[String]] = []
        var queryFailure = false
        var serviceQueryFailure = false
        var foreignPath = false
        var malformedOverrides = false
        var ignoreDisable = false
        var ignoreBootout = false
        var failNextBootstrap = false

        init(path: String, label: String = "org.evict.fixture", loaded: Bool = true, disabled: Bool? = nil) {
            self.path = path; self.label = label; self.loaded = loaded; self.disabled = disabled
        }

        func run(_ executable: String, _ args: [String]) -> Shell.Output {
            lock.lock(); defer { lock.unlock() }
            calls.append(args)
            switch args[0] {
            case "print-disabled":
                if queryFailure { return .init(status: 13, stdout: "", stderr: "Permission denied") }
                if malformedOverrides { return .init(status: 0, stdout: "unrecognized output", stderr: "") }
                let row = disabled.map { "\"\(label)\" => \($0 ? "true" : "false")\n" } ?? ""
                return .init(status: 0, stdout: "disabled services = {\n" + row + "}\n", stderr: "")
            case "print":
                if queryFailure || serviceQueryFailure { return .init(status: 124, stdout: "", stderr: "Timed out") }
                if !loaded { return .init(status: 113, stdout: "", stderr: "Could not find service \"\(label)\"") }
                return .init(status: 0, stdout: args[1] + " = {\n path = \(foreignPath ? "/somewhere/else.plist" : path)\n}", stderr: "")
            case "enable": disabled = false
            case "disable": if !ignoreDisable { disabled = true }
            case "bootstrap":
                if failNextBootstrap { failNextBootstrap = false; return .init(status: 5, stdout: "", stderr: "Bootstrap failed") }
                if disabled == true { return .init(status: 5, stdout: "", stderr: "Disabled") }
                loaded = true
            case "bootout": if !ignoreBootout { loaded = false }
            default: break
            }
            return .init(status: 0, stdout: "", stderr: "")
        }

        var mutations: [[String]] { calls.filter { ["enable", "disable", "bootstrap", "bootout"].contains($0[0]) } }
    }

    private func fixture(_ body: (URL) throws -> Void) throws {
        let directory = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("EvictStartup-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        try body(directory)
    }

    private func job(_ directory: URL, label: String = "org.evict.fixture", disabled: Bool = false,
                     program: String = "/bin/sh") throws -> StartupItem {
        let root = directory.appendingPathComponent("LaunchAgents")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let url = root.appendingPathComponent(label + ".plist")
        let data = try PropertyListSerialization.data(fromPropertyList: ["Label": label, "ProgramArguments": [program, "fixture"],
                                                                       "RunAtLoad": true, "Disabled": disabled], format: .xml, options: 0)
        try data.write(to: url)
        return StartupItem(path: url.path, label: label, program: program, isDaemon: false,
                           isSystemScope: false, runsAtLoad: true, isOrphan: !FileManager.default.isExecutableFile(atPath: program))
    }

    private func controller(_ item: StartupItem, backend: Launchd,
                            trash: @escaping @Sendable (String) throws -> Int64 = { _ in XCTFail("Unexpected Trash action"); return 0 }) -> StartupAgentControl {
        StartupAgentControl(root: (item.path as NSString).deletingLastPathComponent,
                            runner: { backend.run($0, $1) }, moveToTrash: trash)
    }

    func testDisableAndEnableAreVerifiedAndKeepPlist() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            XCTAssertTrue(initial.isLoaded)
            XCTAssertFalse(initial.isDisabled)
            let disabled = try control.setEnabled(false, item: item, expected: initial)
            XCTAssertTrue(disabled.isDisabled)
            XCTAssertFalse(disabled.isLoaded)
            XCTAssertTrue(FileManager.default.fileExists(atPath: item.path))
            let enabled = try control.setEnabled(true, item: item, expected: disabled)
            XCTAssertFalse(enabled.isDisabled)
            XCTAssertTrue(enabled.isLoaded)
            XCTAssertEqual(backend.mutations.map { $0[0] }, ["disable", "bootout", "enable", "bootstrap"])
        }
    }

    func testPlistDisabledDefaultIsRespectedWithoutAnOverride() throws {
        try fixture { directory in
            let item = try job(directory, disabled: true)
            let backend = Launchd(path: item.path, loaded: false)
            let control = controller(item, backend: backend)
            XCTAssertTrue(try control.inspect(item).isDisabled)
            backend.disabled = false
            XCTAssertFalse(try control.inspect(item).isDisabled)
        }
    }

    func testMalformedOverridesAndQueryFailuresNeverMeanDisabledOrAbsent() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            backend.queryFailure = true
            XCTAssertThrowsError(try control.setEnabled(false, item: item, expected: initial))
            XCTAssertTrue(backend.mutations.isEmpty)
            backend.queryFailure = false
            backend.serviceQueryFailure = true
            XCTAssertThrowsError(try control.setEnabled(false, item: item, expected: initial))
            XCTAssertTrue(backend.mutations.isEmpty)
            backend.serviceQueryFailure = false
            backend.malformedOverrides = true
            XCTAssertThrowsError(try control.inspect(item))
            XCTAssertTrue(backend.mutations.isEmpty)
        }
    }

    func testDifferentLoadedJobWithSameLabelIsUntouched() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            backend.foreignPath = true
            XCTAssertThrowsError(try controller(item, backend: backend).inspect(item))
            XCTAssertTrue(backend.mutations.isEmpty)
        }
    }

    func testChangedPlistRequiresNewReviewBeforeMutation() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            var changed = try XCTUnwrap(Plist.read(atPath: item.path))
            changed["RunAtLoad"] = false
            let data = try PropertyListSerialization.data(fromPropertyList: changed, format: .xml, options: 0)
            try data.write(to: URL(fileURLWithPath: item.path))
            XCTAssertThrowsError(try control.setEnabled(false, item: item, expected: initial))
            XCTAssertTrue(backend.mutations.isEmpty)
        }
    }

    func testAppleScheduleForeignOwnerAndLinkedJobsAreRefused() throws {
        try fixture { directory in
            for label in ["com.apple.fixture", ScanScheduler.label] {
                let item = try job(directory, label: label)
                let backend = Launchd(path: item.path, label: label)
                XCTAssertThrowsError(try controller(item, backend: backend).inspect(item))
                XCTAssertTrue(backend.calls.isEmpty)
            }
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let foreignOwner = StartupAgentControl(root: (item.path as NSString).deletingLastPathComponent,
                userID: getuid() + 1, runner: { backend.run($0, $1) })
            XCTAssertThrowsError(try foreignOwner.inspect(item))
            XCTAssertTrue(backend.calls.isEmpty)
            let moved = directory.appendingPathComponent("original.plist")
            try FileManager.default.moveItem(atPath: item.path, toPath: moved.path)
            try FileManager.default.createSymbolicLink(atPath: item.path, withDestinationPath: moved.path)
            XCTAssertThrowsError(try controller(item, backend: backend).inspect(item))
            XCTAssertTrue(backend.calls.isEmpty)
        }
    }

    func testUnconfirmedDisableRestoresPreviousLoadedState() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            backend.ignoreDisable = true
            XCTAssertThrowsError(try control.setEnabled(false, item: item, expected: initial)) { error in
                XCTAssertTrue(error.localizedDescription.contains("previous startup state was restored"))
            }
            XCTAssertTrue(backend.loaded)
            XCTAssertEqual(backend.disabled, false)
        }
    }

    func testBootstrapFailureRestoresDisabledUnloadedState() throws {
        try fixture { directory in
            let item = try job(directory, disabled: true)
            let backend = Launchd(path: item.path, loaded: false, disabled: true)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            backend.failNextBootstrap = true
            XCTAssertThrowsError(try control.setEnabled(true, item: item, expected: initial))
            XCTAssertFalse(backend.loaded)
            XCTAssertEqual(backend.disabled, true)
        }
    }

    func testTrashRunsOnlyAfterVerifiedBootout() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let trashed = directory.appendingPathComponent("Trashed.plist")
            let control = controller(item, backend: backend) { path in
                XCTAssertFalse(backend.loaded)
                let size = FileSize.fileBytes(path)
                try FileManager.default.moveItem(atPath: path, toPath: trashed.path)
                return size
            }
            let initial = try control.inspect(item)
            XCTAssertGreaterThan(try control.trash(item, expected: initial), 0)
            XCTAssertFalse(FileManager.default.fileExists(atPath: item.path))
            XCTAssertTrue(FileManager.default.fileExists(atPath: trashed.path))
            XCTAssertFalse(backend.loaded)
        }
    }

    func testStillLoadedJobCannotReachTrash() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            backend.ignoreBootout = true
            XCTAssertThrowsError(try control.trash(item, expected: initial))
            XCTAssertTrue(FileManager.default.fileExists(atPath: item.path))
            XCTAssertTrue(backend.loaded)
        }
    }

    func testTrashFailureRestoresRegistrationAndKeepsFile() throws {
        try fixture { directory in
            let item = try job(directory)
            let backend = Launchd(path: item.path)
            let control = controller(item, backend: backend) { _ in
                throw NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError)
            }
            let initial = try control.inspect(item)
            XCTAssertThrowsError(try control.trash(item, expected: initial)) { error in
                XCTAssertTrue(error.localizedDescription.contains("previous startup state was restored"))
            }
            XCTAssertTrue(FileManager.default.fileExists(atPath: item.path))
            XCTAssertTrue(backend.loaded)
        }
    }

    func testMissingProgramCanBeDisabledButNotEnabled() throws {
        try fixture { directory in
            let item = try job(directory, program: directory.appendingPathComponent("gone").path)
            let backend = Launchd(path: item.path, loaded: false)
            let control = controller(item, backend: backend)
            let initial = try control.inspect(item)
            XCTAssertFalse(initial.canEnable)
            XCTAssertThrowsError(try control.setEnabled(true, item: item, expected: initial))
            XCTAssertTrue(backend.mutations.isEmpty)
            XCTAssertTrue(try control.setEnabled(false, item: item, expected: initial).isDisabled)
        }
    }

    func testDisabledParserRejectsDuplicateAndPartialValues() throws {
        XCTAssertEqual(try StartupAgentControl.parseDisabledServices("disabled services = {\n \"org.fixture\" => false\n}"), ["org.fixture": false])
        XCTAssertThrowsError(try StartupAgentControl.parseDisabledServices("disabled services = {\n \"org.fixture\" => maybe\n}"))
        XCTAssertThrowsError(try StartupAgentControl.parseDisabledServices("disabled services = {\n \"org.fixture\" => false\n \"org.fixture\" => true\n}"))
        XCTAssertFalse(StartupAgentControl.validLabel("org.fixture/other"))
    }

    #if os(macOS)
    func testNativeLaunchctlCanInspectAnAbsentFixtureWithoutChangingJobs() throws {
        let domain = "gui/\(getuid())"
        let output = Shell.run("/bin/launchctl", ["print-disabled", domain], timeout: 10)
        guard output.ok else { throw XCTSkip("No readable GUI launchd domain is available on this runner.") }
        XCTAssertNoThrow(try StartupAgentControl.parseDisabledServices(output.stdout))
        try fixture { directory in
            let item = try job(directory, label: "org.evict.fixture." + UUID().uuidString)
            let control = StartupAgentControl(root: (item.path as NSString).deletingLastPathComponent,
                runner: { Shell.run($0, $1, timeout: 10) })
            let inspected = try control.inspect(item)
            XCTAssertFalse(inspected.isLoaded)
            XCTAssertFalse(inspected.isDisabled)
        }
    }
    #endif
}
