import Foundation

public struct MacReleaseBundleMetadata: Equatable, Sendable {
    public let version: MacReleaseVersion
    public let build: String
    public let minimumSystemVersion: String

    public static func read(_ data: Data, expectedVersion: String) throws -> Self {
        guard let values = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              values["CFBundleIdentifier"] as? String == "app.evict.mac",
              values["CFBundleExecutable"] as? String == "Evict",
              values["CFBundlePackageType"] as? String == "APPL",
              let shortVersion = values["CFBundleShortVersionString"] as? String,
              let version = MacReleaseVersion(values["EvictReleaseVersion"] as? String ?? shortVersion),
              version.rawValue == expectedVersion,
              shortVersion == version.core || shortVersion == version.rawValue,
              let build = values["CFBundleVersion"] as? String, !build.isEmpty,
              build.allSatisfy({ $0.isASCII && $0.isNumber }),
              let minimum = values["LSMinimumSystemVersion"] as? String else {
            throw MacReleaseError.invalid("The application identity or native version does not match the selected release.")
        }
        let parts = minimum.split(separator: ".").compactMap { Int($0) }
        guard (1...3).contains(parts.count), parts.count == minimum.split(separator: ".").count,
              parts.allSatisfy({ $0 >= 0 && $0 < 1000 }) else {
            throw MacReleaseError.invalid("The application's minimum macOS version is invalid.")
        }
        return Self(version: version, build: build, minimumSystemVersion: minimum)
    }
}

public struct PreparedMacRelease: Identifiable, Sendable {
    public let entry: MacReleaseHistoryEntry
    public let applicationURL: URL
    public var id: UUID { entry.id }
}

#if os(macOS)
/// Preparation never overwrites the running app. Finder performs the explicitly confirmed
/// replacement, keeps macOS's authorization/Gatekeeper checks, and a later launch verifies it.
public enum MacReleasePreparation {
    public static func prepare(archive: URL, release: MacRelease, currentBundle: URL,
                               currentVersion: String, store: MacReleaseStore) throws -> PreparedMacRelease {
        try prepareArchive(archive, version: release.version.rawValue, releaseTag: release.tag, operation: .install,
                           currentBundle: currentBundle, currentVersion: currentVersion, store: store, quarantine: true)
    }

    public static func prepareRollback(_ copy: MacReleaseRecoveryCopy, currentBundle: URL,
                                       currentVersion: String, store: MacReleaseStore) throws -> PreparedMacRelease {
        let archive = store.recoveryArchive(for: copy.id)
        try requireRegularFile(archive)
        let size = (try FileManager.default.attributesOfItem(atPath: archive.path)[.size] as? NSNumber)?.int64Value ?? 0
        guard size == copy.archiveBytes, size > 0, size <= MacReleasePolicy.maximumArchiveBytes,
              try MacReleaseIntegrity.sha256(of: archive) == copy.archiveDigest else {
            throw MacReleaseError.invalid("This recovery copy is missing, incomplete or has changed. Rollback was stopped.")
        }
        return try prepareArchive(archive, version: copy.version, releaseTag: nil, operation: .rollback,
                                  currentBundle: currentBundle, currentVersion: currentVersion, store: store, quarantine: false)
    }

    public static func validatePrepared(_ entry: MacReleaseHistoryEntry, store: MacReleaseStore) throws -> PreparedMacRelease {
        guard [.prepared, .handedOff].contains(entry.status) else { throw MacReleaseError.invalid("This installation is no longer pending.") }
        let application = store.stagingDirectory(for: entry.id).appendingPathComponent("Extracted/Evict.app", isDirectory: true)
        let digest = try validateNativeBundle(application, version: entry.toVersion)
        guard digest == entry.executableDigest else { throw MacReleaseError.invalid("The staged application has changed. Prepare it again before installing.") }
        return PreparedMacRelease(entry: entry, applicationURL: application)
    }

    /// Both architectures are required because the native release is advertised as universal.
    @discardableResult
    public static func validateNativeBundle(_ application: URL, version: String) throws -> String {
        try requireOrdinaryTree(application)
        let info = application.appendingPathComponent("Contents/Info.plist")
        try requireRegularFile(info)
        let metadata = try MacReleaseBundleMetadata.read(Data(contentsOf: info), expectedVersion: version)
        let minimum = metadata.minimumSystemVersion.split(separator: ".").compactMap { Int($0) }
        let required = OperatingSystemVersion(majorVersion: minimum[0], minorVersion: minimum.count > 1 ? minimum[1] : 0,
                                             patchVersion: minimum.count > 2 ? minimum[2] : 0)
        guard ProcessInfo.processInfo.isOperatingSystemAtLeast(required) else {
            throw MacReleaseError.invalid("This release requires macOS \(metadata.minimumSystemVersion) or later.")
        }
        let executable = application.appendingPathComponent("Contents/MacOS/Evict")
        try requireRegularFile(executable)
        guard FileManager.default.isExecutableFile(atPath: executable.path) else { throw MacReleaseError.invalid("The downloaded application is not executable.") }
        let architectures = Shell.run("/usr/bin/lipo", ["-archs", executable.path])
        let names = Set(architectures.stdout.split(whereSeparator: \.isWhitespace).map(String.init))
        guard architectures.ok, names.contains("arm64"), names.contains("x86_64") else {
            throw MacReleaseError.invalid("This release does not contain the promised Apple Silicon and Intel binaries.")
        }
        let signature = Shell.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", "--verbose=2", application.path], timeout: 60)
        guard signature.ok else { throw MacReleaseError.invalid("macOS could not verify the application's code signature. Installation was stopped.") }
        return try MacReleaseIntegrity.sha256(of: executable)
    }

    private static func prepareArchive(_ archive: URL, version: String, releaseTag: String?, operation: MacReleaseOperation,
                                       currentBundle: URL, currentVersion: String, store: MacReleaseStore,
                                       quarantine: Bool) throws -> PreparedMacRelease {
        guard store.lastError == nil else { throw MacReleaseError.file(store.lastError!) }
        try Task.checkCancellation()
        guard MacReleaseVersion(version) != nil, MacReleaseVersion(currentVersion) != nil,
              currentBundle.pathExtension.lowercased() == "app" else {
            throw MacReleaseError.invalid("Run the packaged Evict.app before preparing an update or rollback.")
        }
        _ = try MacReleaseArchive.inspect(archive)
        let id = UUID(), stage = store.stagingDirectory(for: id)
        try makePrivateDirectory(stage, inside: store.directory)
        let extracted = stage.appendingPathComponent("Extracted", isDirectory: true)
        var committed = false
        defer { if !committed { try? FileManager.default.removeItem(at: stage) } }
        try FileManager.default.createDirectory(at: extracted, withIntermediateDirectories: false)
        let result = Shell.run("/usr/bin/ditto", ["-x", "-k", archive.path, extracted.path], timeout: 120)
        guard result.ok else { throw MacReleaseError.file("The verified application could not be extracted: \(result.stderr)") }
        let application = extracted.appendingPathComponent("Evict.app", isDirectory: true)
        let digest = try validateNativeBundle(application, version: version)
        try Task.checkCancellation()
        if quarantine {
            // URLSession does not attach quarantine automatically. Preserve the normal first-open
            // approval instead of removing or bypassing macOS's downloaded-application checks.
            let value = "0081;\(String(Int(Date().timeIntervalSince1970), radix: 16));Evict;\(id.uuidString)"
            let result = Shell.run("/usr/bin/xattr", ["-w", "com.apple.quarantine", value, application.path])
            guard result.ok else { throw MacReleaseError.file("macOS download protection could not be applied to the staged application.") }
        }
        let recovery = try makeRecoveryCopy(currentBundle: currentBundle, version: currentVersion, store: store)
        var recoveryCommitted = false
        defer { if !recoveryCommitted { try? FileManager.default.removeItem(at: store.recoveryArchive(for: recovery.id)) } }
        try Task.checkCancellation()
        let destination = installationDestination(currentBundle)
        let entry = MacReleaseHistoryEntry(id: id, operation: operation, fromVersion: currentVersion, toVersion: version,
                                          releaseTag: releaseTag,
                                          detail: "Download, archive paths, native version, universal architectures and code signature verified. Recovery copy saved. Awaiting explicit installation in Finder.",
                                          installationPath: destination.path, executableDigest: digest)
        try store.update { state in
            for index in state.history.indices where [.prepared, .handedOff].contains(state.history[index].status) {
                state.history[index].status = .cancelled
                state.history[index].detail = "Superseded by a different explicitly selected version."
            }
            state.recoveryCopies.insert(recovery, at: 0)
            state.history.insert(entry, at: 0)
        }
        recoveryCommitted = true
        committed = true
        return PreparedMacRelease(entry: entry, applicationURL: application)
    }

    private static func makeRecoveryCopy(currentBundle: URL, version: String, store: MacReleaseStore) throws -> MacReleaseRecoveryCopy {
        // Recovery copies obey the same native identity and architecture rules as releases.
        _ = try validateNativeBundle(currentBundle, version: version)
        let id = UUID(), work = store.stagingDirectory(for: id)
        try makePrivateDirectory(work, inside: store.directory)
        defer { try? FileManager.default.removeItem(at: work) }
        let copy = work.appendingPathComponent("Evict.app", isDirectory: true)
        let copied = Shell.run("/usr/bin/ditto", [currentBundle.path, copy.path], timeout: 120)
        guard copied.ok else { throw MacReleaseError.file("A recovery copy could not be made. Your installed application has not changed.") }
        let archive = store.recoveryArchive(for: id)
        try makePrivateDirectory(archive.deletingLastPathComponent(), inside: store.directory, allowExisting: true)
        let zipped = Shell.run("/usr/bin/ditto", ["-c", "-k", "--sequesterRsrc", "--keepParent", copy.path, archive.path], timeout: 120)
        guard zipped.ok else {
            try? FileManager.default.removeItem(at: archive)
            throw MacReleaseError.file("The recovery archive could not be written. Your installed application has not changed.")
        }
        do {
            _ = try MacReleaseArchive.inspect(archive)
            let digest = try MacReleaseIntegrity.sha256(of: archive)
            let size = (try FileManager.default.attributesOfItem(atPath: archive.path)[.size] as? NSNumber)?.int64Value ?? 0
            return MacReleaseRecoveryCopy(id: id, version: version, archiveDigest: digest, archiveBytes: size,
                                          originalInstallationPath: currentBundle.standardizedFileURL.path)
        } catch {
            try? FileManager.default.removeItem(at: archive)
            throw error
        }
    }

    private static func installationDestination(_ currentBundle: URL) -> URL {
        let path = currentBundle.standardizedFileURL.path
        let roots = ["/Applications/", NSHomeDirectory() + "/Applications/"]
        if roots.contains(where: { path.hasPrefix($0) }) { return currentBundle.standardizedFileURL }
        // A DMG or App Translocation is not an installation destination.
        return URL(fileURLWithPath: "/Applications/Evict.app", isDirectory: true)
    }

    private static func makePrivateDirectory(_ url: URL, inside root: URL, allowExisting: Bool = false) throws {
        let manager = FileManager.default
        try manager.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let rootAttributes = try manager.attributesOfItem(atPath: root.path)
        guard rootAttributes[.type] as? FileAttributeType == .typeDirectory,
              url.standardizedFileURL.path.hasPrefix(root.standardizedFileURL.path + "/") else {
            throw MacReleaseError.file("The staging directory is not owned by the version manager.")
        }
        let relative = String(url.standardizedFileURL.path.dropFirst(root.standardizedFileURL.path.count + 1))
        var parent = root
        for component in relative.split(separator: "/") {
            parent.appendPathComponent(String(component), isDirectory: true)
            if manager.fileExists(atPath: parent.path) {
                let attributes = try manager.attributesOfItem(atPath: parent.path)
                guard attributes[.type] as? FileAttributeType == .typeDirectory,
                      parent != url || allowExisting else {
                    throw MacReleaseError.file("A staging directory has been replaced or is already in use.")
                }
            } else {
                try manager.createDirectory(at: parent, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            }
        }
    }

    private static func requireRegularFile(_ url: URL) throws {
        guard try FileManager.default.attributesOfItem(atPath: url.path)[.type] as? FileAttributeType == .typeRegular else {
            throw MacReleaseError.invalid("A required application file is a link or special file.")
        }
    }

    private static func requireOrdinaryTree(_ root: URL) throws {
        let manager = FileManager.default
        guard try manager.attributesOfItem(atPath: root.path)[.type] as? FileAttributeType == .typeDirectory,
              let entries = manager.enumerator(at: root, includingPropertiesForKeys: nil) else {
            throw MacReleaseError.invalid("The application bundle is not a regular directory.")
        }
        for case let url as URL in entries {
            let type = try manager.attributesOfItem(atPath: url.path)[.type] as? FileAttributeType
            guard type == .typeDirectory || type == .typeRegular else {
                throw MacReleaseError.invalid("The application bundle contains a link or special file.")
            }
        }
    }
}
#endif
