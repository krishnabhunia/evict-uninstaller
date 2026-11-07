import Foundation

public struct SoftwareUpdateCommandOutput: Sendable {
    public let status: Int32
    public let stdout: String
    public let stderr: String

    public init(status: Int32, stdout: String = "", stderr: String = "") {
        self.status = status
        self.stdout = stdout
        self.stderr = stderr
    }
}

public struct SoftwareUpdateAppEvidence: Sendable {
    public let version: String?
    public let build: String?
    public let paths: [String]

    public init(version: String? = nil, build: String? = nil, paths: [String] = []) {
        self.version = version
        self.build = build
        self.paths = paths
    }
}

/// Uses Homebrew's installed inventory as the authority for package identity. Outdated records
/// alone never authorize installation, and ambiguous tokens from multiple taps fail closed.
public enum HomebrewUpdateParser {
    struct InstalledCask: Sendable {
        let caskID: String
        let token: String
        let name: String
        let recordedVersion: String
        let availableVersion: String
        let appVersion: String?
        let appBuild: String?
        let appPaths: [String]
        let homepage: URL?
        let isPinned: Bool
        let isDisabled: Bool

        var candidate: SoftwareUpdateCandidate {
            SoftwareUpdateCandidate(caskID: caskID, name: name, recordedVersion: recordedVersion,
                                    availableVersion: availableVersion, appVersion: appVersion,
                                    appBuild: appBuild, appPaths: appPaths, homepage: homepage,
                                    isPinned: isPinned, isDisabled: isDisabled)
        }
    }

    public static func isValidCaskID(_ value: String) -> Bool {
        guard value.count <= 240 else { return false }
        let parts = value.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 1 || parts.count == 3 else { return false }
        return parts.allSatisfy {
            $0.range(of: #"^[a-z0-9][a-z0-9+._@-]*$"#, options: .regularExpression) != nil
        }
    }

    public static func candidates(inventoryJSON: String, outdatedJSON: String) throws -> [SoftwareUpdateCandidate] {
        let inventory = try inventory(inventoryJSON)
        return try candidates(inventory: inventory, outdatedJSON: outdatedJSON)
    }

    static func inventory(_ json: String,
                          evidence: (String, String, [String]) -> SoftwareUpdateAppEvidence = { _, _, _ in .init() }) throws -> [InstalledCask] {
        let rows = try casks(json)
        var seen: Set<String> = []
        var result: [InstalledCask] = []
        for row in rows {
            guard let token = nonempty(row["token"]), isValidCaskID(token), !token.contains("/") else {
                throw SoftwareUpdateError.invalidResponse("A cask has an invalid token.")
            }
            let fullToken = nonempty(row["full_token"])
            let tap = nonempty(row["tap"]) ?? "homebrew/cask"
            let caskID = (fullToken?.contains("/") == true ? fullToken! : tap + "/" + token)
            guard isValidCaskID(caskID), caskID.split(separator: "/").last.map(String.init) == token else {
                throw SoftwareUpdateError.invalidResponse("A cask has an invalid fully qualified identity.")
            }
            if let fullToken {
                guard fullToken == token || fullToken == tap + "/" + token else {
                    throw SoftwareUpdateError.invalidResponse("The cask token and tap disagree.")
                }
            }
            guard row.keys.contains("installed") else {
                throw SoftwareUpdateError.invalidResponse("The installed-state field is missing for \(token).")
            }
            let installed = try installedVersion(row["installed"])
            guard let installed else { continue }
            guard seen.insert(caskID).inserted else {
                throw SoftwareUpdateError.invalidResponse("Duplicate installed package \(caskID).")
            }
            guard let available = nonempty(row["version"]) else {
                throw SoftwareUpdateError.invalidResponse("No available version was reported for \(token).")
            }
            let names = row["name"] as? [String]
            let name = names?.first.flatMap { nonempty($0) } ?? token
            let appEvidence = evidence(caskID, installed, appArtifacts(row["artifacts"]))
            let appVersion = appEvidence.version ?? nonempty(row["bundle_short_version"])
            let appBuild = appEvidence.build ?? nonempty(row["bundle_version"])
            result.append(InstalledCask(caskID: caskID, token: token, name: name,
                                        recordedVersion: installed, availableVersion: available,
                                        appVersion: appVersion, appBuild: appBuild, appPaths: appEvidence.paths,
                                        homepage: safeHomepage(row["homepage"]), isPinned: row["pinned"] as? Bool ?? false,
                                        isDisabled: row["disabled"] as? Bool ?? false))
        }
        return result
    }

    static func candidates(inventory: [InstalledCask], outdatedJSON: String) throws -> [SoftwareUpdateCandidate] {
        let rows = try casks(outdatedJSON)
        var result: [SoftwareUpdateCandidate] = []
        var seen: Set<String> = []
        for row in rows {
            guard let name = nonempty(row["name"]), isValidCaskID(name),
                  let current = nonempty(row["current_version"]),
                  let reportedInstalled = row["installed_versions"] as? [String],
                  !reportedInstalled.isEmpty, reportedInstalled.allSatisfy({ nonempty($0) != nil }) else {
                throw SoftwareUpdateError.invalidResponse("An outdated cask is missing version information.")
            }
            let matches = inventory.filter { name.contains("/") ? $0.caskID == name : $0.token == name }
            // A stale entry for an uninstalled package must never become an install request.
            guard !matches.isEmpty else { continue }
            guard matches.count == 1, let installed = matches.first else {
                throw SoftwareUpdateError.invalidResponse("More than one installed tap uses the token \(name).")
            }
            guard current == installed.availableVersion else {
                throw SoftwareUpdateError.invalidResponse("The catalogue changed during the scan. Check again before updating \(name).")
            }
            guard seen.insert(installed.caskID).inserted else { continue }
            let candidate = installed.candidate
            result.append(SoftwareUpdateCandidate(caskID: candidate.caskID, name: candidate.name,
                                                  recordedVersion: candidate.recordedVersion,
                                                  availableVersion: candidate.availableVersion,
                                                  appVersion: candidate.appVersion, appBuild: candidate.appBuild,
                                                  appPaths: candidate.appPaths, homepage: candidate.homepage,
                                                  isPinned: candidate.isPinned || (row["pinned"] as? Bool ?? false),
                                                  isDisabled: candidate.isDisabled))
        }
        return result.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    private static func casks(_ json: String) throws -> [[String: Any]] {
        guard let data = json.data(using: .utf8), data.count <= 32 * 1_024 * 1_024,
              let object = try? JSONSerialization.jsonObject(with: data),
              let root = object as? [String: Any], let rows = root["casks"] as? [[String: Any]] else {
            throw SoftwareUpdateError.invalidResponse("Expected the Homebrew JSON v2 casks array.")
        }
        return rows
    }

    private static func installedVersion(_ value: Any?) throws -> String? {
        guard let value, !(value is NSNull) else { return nil }
        if let text = value as? String { return nonempty(text) }
        // Accept inventory producers that report a one-element version array, but never guess
        // which version is active when several different installations are reported.
        if let versions = value as? [String] {
            let unique = Set(versions.compactMap { nonempty($0) })
            if unique.isEmpty { return nil }
            if unique.count == 1 { return unique.first }
        }
        throw SoftwareUpdateError.invalidResponse("The active installed cask version could not be determined.")
    }

    private static func appArtifacts(_ value: Any?) -> [String] {
        guard let rows = value as? [[String: Any]] else { return [] }
        return rows.compactMap { row in
            guard let app = row["app"] as? [Any], let source = app.first as? String else { return nil }
            let target = app.compactMap { $0 as? [String: Any] }.compactMap { $0["target"] as? String }.first
            let value = target ?? source
            guard !value.hasPrefix("/"), value.hasSuffix(".app"),
                  !value.split(separator: "/", omittingEmptySubsequences: false).contains(where: { $0 == ".." || $0.isEmpty }) else { return nil }
            return value
        }
    }

    private static func safeHomepage(_ value: Any?) -> URL? {
        guard let text = nonempty(value), let url = URL(string: text),
              url.scheme?.lowercased() == "https", url.host != nil, url.user == nil, url.password == nil else { return nil }
        return url
    }

    private static func nonempty(_ value: Any?) -> String? {
        guard let text = value as? String else { return nil }
        let result = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return result.isEmpty ? nil : result
    }
}

/// All commands use an executable path plus argv; no package name is evaluated by a shell.
/// Homebrew handles download checksums and macOS installer policies. Evict never passes
/// `--force`, `--no-quarantine`, `--zap` or an administrator password.
public struct HomebrewSoftwareUpdateService: Sendable {
    public typealias Runner = @Sendable (String, [String], TimeInterval, [String: String]) -> SoftwareUpdateCommandOutput
    public typealias EvidenceReader = @Sendable (String, String, [String]) -> SoftwareUpdateAppEvidence

    public let brewPath: String?
    private let runner: Runner
    private let evidenceReader: EvidenceReader

    public init(brewPath: String? = HomebrewService.brewPath,
                runner: @escaping Runner = { executable, arguments, timeout, environment in
                    let result = Shell.run(executable, arguments, timeout: timeout, environment: environment)
                    return .init(status: result.status, stdout: result.stdout, stderr: result.stderr)
                }, evidenceReader: EvidenceReader? = nil) {
        self.brewPath = brewPath
        self.runner = runner
        self.evidenceReader = evidenceReader ?? { id, version, artifacts in
            Self.readAppEvidence(brewPath: brewPath, caskID: id, installedVersion: version, artifacts: artifacts)
        }
    }

    public func checkForUpdates(refreshCatalog: Bool = true) throws -> SoftwareUpdateScan {
        guard let brewPath else { throw SoftwareUpdateError.homebrewNotInstalled }
        if refreshCatalog {
            _ = try run(brewPath, ["update", "--quiet"], step: "Refreshing Homebrew", timeout: 180)
        }
        let inventory = try installedCasks()
        let outdated = try run(brewPath, ["outdated", "--cask", "--json=v2"], step: "Checking Homebrew updates", timeout: 120)
        return SoftwareUpdateScan(candidates: try HomebrewUpdateParser.candidates(inventory: inventory, outdatedJSON: outdated.stdout),
                                  managedCaskCount: inventory.count, checkedAt: Date())
    }

    /// The selected version is a consent boundary. If the catalogue or installation changed,
    /// return to review instead of silently upgrading to a newly discovered target.
    public func update(_ approved: SoftwareUpdateCandidate) -> SoftwareUpdateResult {
        guard let brewPath else { return .init(status: .blocked, details: SoftwareUpdateError.homebrewNotInstalled.localizedDescription) }
        guard HomebrewUpdateParser.isValidCaskID(approved.caskID) else {
            return .init(status: .blocked, details: SoftwareUpdateError.invalidCaskID.localizedDescription)
        }
        do {
            let before = try installedCasks()
            guard let installed = before.first(where: { $0.caskID == approved.caskID }) else {
                return .init(status: .blocked, details: "This package is no longer installed. No update was started.")
            }
            if installed.recordedVersion == approved.availableVersion,
               installed.appVersion == nil || SoftwareUpdateVersion.matchesApp(shortVersion: installed.appVersion!, build: installed.appBuild, target: approved.availableVersion) {
                return .init(status: .alreadyCurrent, recordedVersionAfter: installed.recordedVersion,
                             appVersionAfter: installed.appVersion, details: "The requested version is already installed. No update was needed.")
            }
            guard installed.availableVersion == approved.availableVersion,
                  installed.recordedVersion == approved.recordedVersion,
                  installed.appVersion == approved.appVersion, installed.appBuild == approved.appBuild else {
                return .init(status: .blocked, recordedVersionAfter: installed.recordedVersion, appVersionAfter: installed.appVersion,
                             details: "The installed or available version changed after your selection. Check again and review the new versions.")
            }
            if let reason = installed.candidate.blockedReason {
                return .init(status: .blocked, recordedVersionAfter: installed.recordedVersion, appVersionAfter: installed.appVersion, details: reason)
            }
            let outdated = try run(brewPath, ["outdated", "--cask", "--json=v2"], step: "Rechecking the selected update", timeout: 120)
            let currentCandidates = try HomebrewUpdateParser.candidates(inventory: before, outdatedJSON: outdated.stdout)
            guard let current = currentCandidates.first(where: { $0.caskID == approved.caskID }), current.blockedReason == nil else {
                return .init(status: .blocked, details: "Homebrew no longer offers this update, or it is pinned. Check again before updating.")
            }
            let output = runner(brewPath, ["upgrade", "--cask", approved.caskID], 900, Self.environment)
            let diagnostics = Self.diagnostics(output)
            let after: HomebrewUpdateParser.InstalledCask?
            do {
                after = try installedCasks().first { $0.caskID == approved.caskID }
            } catch {
                return .init(status: output.status == 0 ? .unverified : .failed, exitStatus: output.status,
                             details: "\(diagnostics)\nThe installed version could not be reread: \(error.localizedDescription)")
            }
            guard output.status == 0 else {
                return .init(status: .failed, recordedVersionAfter: after?.recordedVersion, appVersionAfter: after?.appVersion,
                             exitStatus: output.status, details: diagnostics + "\nReview the provider output before trying again. Installers that require a terminal or administrator interaction must be completed outside Evict.")
            }
            guard let after, after.recordedVersion == approved.availableVersion else {
                return .init(status: .unverified, recordedVersionAfter: after?.recordedVersion, appVersionAfter: after?.appVersion,
                             exitStatus: output.status, details: diagnostics + "\nHomebrew exited successfully, but its installed version does not match the version you approved.")
            }
            if approved.appVersion != nil && after.appVersion == nil {
                return .init(status: .unverified, recordedVersionAfter: after.recordedVersion,
                             exitStatus: output.status, details: diagnostics + "\nThe app previously reported a version, but its metadata can no longer be read. The update is not marked verified.")
            }
            if let observed = after.appVersion,
               !SoftwareUpdateVersion.matchesApp(shortVersion: observed, build: after.appBuild, target: approved.availableVersion) {
                return .init(status: .unverified, recordedVersionAfter: after.recordedVersion, appVersionAfter: observed,
                             exitStatus: output.status, details: diagnostics + "\nThe installed app's own version differs from the selected Homebrew version. The update is not marked verified.")
            }
            let evidence = after.appVersion == nil
                ? "Homebrew's installation record now matches the selected version. This cask does not expose an app version."
                : "The app version and Homebrew installation record match the selected version."
            return .init(status: .updated, recordedVersionAfter: after.recordedVersion, appVersionAfter: after.appVersion,
                         exitStatus: output.status, details: evidence + "\n" + diagnostics)
        } catch {
            return .init(status: .blocked, details: error.localizedDescription)
        }
    }

    private func installedCasks() throws -> [HomebrewUpdateParser.InstalledCask] {
        guard let brewPath else { throw SoftwareUpdateError.homebrewNotInstalled }
        let output = try run(brewPath, ["info", "--json=v2", "--cask", "--installed"], step: "Reading installed Homebrew apps", timeout: 120)
        return try HomebrewUpdateParser.inventory(output.stdout, evidence: evidenceReader)
    }

    private func run(_ path: String, _ args: [String], step: String, timeout: TimeInterval) throws -> SoftwareUpdateCommandOutput {
        let output = runner(path, args, timeout, Self.environment)
        guard output.status == 0 else {
            throw SoftwareUpdateError.commandFailed(step, output.status, String(Self.diagnostics(output).prefix(2_000)))
        }
        return output
    }

    private static let environment = [
        "HOMEBREW_NO_AUTO_UPDATE": "1", "HOMEBREW_NO_INSTALL_CLEANUP": "1",
        "HOMEBREW_NO_INSTALLED_DEPENDENTS_CHECK": "1", "HOMEBREW_NO_ANALYTICS": "1",
        "HOMEBREW_NO_ENV_HINTS": "1", "NONINTERACTIVE": "1"
    ]

    private static func diagnostics(_ output: SoftwareUpdateCommandOutput) -> String {
        let text = [output.stdout, output.stderr].filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }.joined(separator: "\n")
        return text.isEmpty ? "Homebrew exited with status \(output.status)." : String(text.suffix(24_000))
    }

    /// Follow only the links inside the installed cask's receipt. Matching an unrelated app
    /// merely by display name could read the wrong edition and suggest an unsafe downgrade.
    private static func readAppEvidence(brewPath: String?, caskID: String, installedVersion: String,
                                        artifacts: [String]) -> SoftwareUpdateAppEvidence {
        guard let brewPath, let token = caskID.split(separator: "/").last,
              !installedVersion.contains("/"), installedVersion != ".", installedVersion != ".." else { return .init() }
        let prefix = URL(fileURLWithPath: brewPath).deletingLastPathComponent().deletingLastPathComponent()
        let root = prefix.appendingPathComponent("Caskroom").appendingPathComponent(String(token)).appendingPathComponent(installedVersion)
        var paths: [String] = []
        var versions: [(String?, String?)] = []
        for artifact in artifacts {
            let link = root.appendingPathComponent(artifact)
            guard (try? FileManager.default.destinationOfSymbolicLink(atPath: link.path)) != nil else { continue }
            let target = link.resolvingSymlinksInPath()
            guard target.pathExtension == "app", FileManager.default.fileExists(atPath: target.path) else { continue }
            let info = target.appendingPathComponent("Contents/Info.plist")
            guard let data = try? Data(contentsOf: info),
                  let object = try? PropertyListSerialization.propertyList(from: data, options: [], format: nil),
                  let plist = object as? [String: Any] else { continue }
            paths.append(target.path)
            versions.append((plist["CFBundleShortVersionString"] as? String, plist["CFBundleVersion"] as? String))
        }
        // A suite may have several apps with distinct versions. Do not pretend one is the whole suite.
        return .init(version: versions.count == 1 ? versions.first?.0 : nil,
                     build: versions.count == 1 ? versions.first?.1 : nil, paths: paths)
    }
}
