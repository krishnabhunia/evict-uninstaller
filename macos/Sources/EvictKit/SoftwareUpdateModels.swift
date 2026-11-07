import Foundation

/// A Homebrew receipt and the installed app's own version are different pieces of evidence.
/// Keeping both prevents a self-updated app being presented as an older installation.
public struct SoftwareUpdateCandidate: Identifiable, Equatable, Sendable {
    public var id: String { caskID }
    public let caskID: String
    public let name: String
    public let recordedVersion: String
    public let availableVersion: String
    public let appVersion: String?
    public let appBuild: String?
    public let appPaths: [String]
    public let homepage: URL?
    public let isPinned: Bool
    public let isDisabled: Bool

    public var installedVersion: String { appVersion ?? recordedVersion }
    public var provider: String { "Homebrew" }

    public init(caskID: String, name: String, recordedVersion: String, availableVersion: String,
                appVersion: String? = nil, appBuild: String? = nil, appPaths: [String] = [],
                homepage: URL? = nil, isPinned: Bool = false, isDisabled: Bool = false) {
        self.caskID = caskID
        self.name = name
        self.recordedVersion = recordedVersion
        self.availableVersion = availableVersion
        self.appVersion = appVersion
        self.appBuild = appBuild
        self.appPaths = appPaths
        self.homepage = homepage
        self.isPinned = isPinned
        self.isDisabled = isDisabled
    }

    /// A blocked row is visible with its reason, but cannot enter the install queue.
    public var blockedReason: String? {
        if isPinned { return "Pinned in Homebrew" }
        if isDisabled { return "Disabled by Homebrew" }
        guard HomebrewUpdateParser.isValidCaskID(caskID) else { return "Invalid package identity" }
        guard !recordedVersion.isEmpty else { return "Installed version is unknown" }
        guard !availableVersion.isEmpty, availableVersion != "latest" else {
            return "Homebrew does not report a specific target version"
        }
        if let appVersion {
            if SoftwareUpdateVersion.matchesApp(shortVersion: appVersion, build: appBuild, target: availableVersion) {
                return "App already current; Homebrew record differs"
            }
            if SoftwareUpdateVersion.isClearlyNewer(appVersion, than: availableVersion) {
                return "Installed app is newer than Homebrew's version"
            }
            if let appBuild, SoftwareUpdateVersion.isClearlyNewerBuild(shortVersion: appVersion, build: appBuild, than: availableVersion) {
                return "Installed app build is newer than Homebrew's build"
            }
        }
        if SoftwareUpdateVersion.isClearlyNewer(recordedVersion, than: availableVersion) {
            return "Homebrew's available version is older"
        }
        if recordedVersion == availableVersion { return "Homebrew already records this version" }
        return nil
    }
}

public enum SoftwareUpdateVersion {
    /// Casks commonly encode `short-version,build-number`. Do not invent a semantic version
    /// ordering for vendor strings such as `2025-R2`, `latest` or comma-separated build IDs.
    public static func matchesApp(shortVersion: String, build: String?, target: String) -> Bool {
        let parts = target.split(separator: ",", omittingEmptySubsequences: false).map(String.init)
        guard let first = parts.first, !first.isEmpty, normalize(shortVersion) == normalize(first) else { return false }
        if parts.count == 1 { return true }
        guard parts.count == 2, let build, !build.isEmpty else { return false }
        return normalize(build) == normalize(parts[1])
    }

    /// Only pure dotted numeric versions have an unambiguous ordering here. Arbitrary vendor
    /// versions continue to rely on Homebrew's own outdated check and explicit user selection.
    public static func isClearlyNewer(_ installed: String, than target: String) -> Bool {
        guard let left = numericParts(installed), let right = numericParts(target) else { return false }
        for index in 0..<max(left.count, right.count) {
            let a = index < left.count ? left[index] : 0
            let b = index < right.count ? right[index] : 0
            if a != b { return a > b }
        }
        return false
    }

    public static func isClearlyNewerBuild(shortVersion: String, build: String, than target: String) -> Bool {
        let parts = target.split(separator: ",", omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 2, normalize(shortVersion) == normalize(parts[0]) else { return false }
        return isClearlyNewer(build, than: parts[1])
    }

    private static func normalize(_ value: String) -> String {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.hasPrefix("v") ? String(trimmed.dropFirst()) : trimmed
    }

    private static func numericParts(_ value: String) -> [UInt64]? {
        let normalized = normalize(value.split(separator: ",", omittingEmptySubsequences: false).first.map(String.init) ?? "")
        guard normalized.range(of: #"^[0-9]+(?:\.[0-9]+)*$"#, options: .regularExpression) != nil else { return nil }
        let segments = normalized.split(separator: ".")
        let numbers = segments.compactMap { UInt64($0) }
        return numbers.count == segments.count ? numbers : nil
    }
}

public struct SoftwareUpdateScan: Sendable {
    public let candidates: [SoftwareUpdateCandidate]
    public let managedCaskCount: Int
    public let checkedAt: Date
}

public enum SoftwareUpdateStatus: String, Codable, CaseIterable, Sendable {
    case running, updated, alreadyCurrent, failed, unverified, blocked, interrupted

    public var label: String {
        switch self {
        case .running: return "Updating"
        case .updated: return "Verified"
        case .alreadyCurrent: return "Already current"
        case .failed: return "Failed"
        case .unverified: return "Needs verification"
        case .blocked: return "Not started"
        case .interrupted: return "Interrupted · verify installation"
        }
    }
}

public struct SoftwareUpdateResult: Sendable {
    public let status: SoftwareUpdateStatus
    public let recordedVersionAfter: String?
    public let appVersionAfter: String?
    public let exitStatus: Int32?
    public let details: String

    public init(status: SoftwareUpdateStatus, recordedVersionAfter: String? = nil,
                appVersionAfter: String? = nil, exitStatus: Int32? = nil, details: String) {
        self.status = status
        self.recordedVersionAfter = recordedVersionAfter
        self.appVersionAfter = appVersionAfter
        self.exitStatus = exitStatus
        self.details = details
    }
}

public struct SoftwareUpdateHistoryEntry: Identifiable, Codable, Equatable, Sendable {
    public let id: UUID
    public let date: Date
    public var completedAt: Date?
    public let caskID: String
    public let name: String
    public let recordedVersionBefore: String
    public let appVersionBefore: String?
    public let targetVersion: String
    public var recordedVersionAfter: String?
    public var appVersionAfter: String?
    public var status: SoftwareUpdateStatus
    public var exitStatus: Int32?
    public var details: String

    public init(candidate: SoftwareUpdateCandidate, date: Date = Date(), id: UUID = UUID()) {
        self.id = id
        self.date = date
        caskID = candidate.caskID
        name = candidate.name
        recordedVersionBefore = candidate.recordedVersion
        appVersionBefore = candidate.appVersion
        targetVersion = candidate.availableVersion
        status = .running
        details = "Update requested. The installed version has not yet been verified."
    }

    public var installedVersionBefore: String { appVersionBefore ?? recordedVersionBefore }
    public var installedVersionAfter: String? { appVersionAfter ?? recordedVersionAfter }
}

public struct SoftwareUpdateExclusion: Identifiable, Codable, Equatable, Sendable {
    public var id: String { caskID }
    public let caskID: String
    public let name: String
    public let addedAt: Date

    public init(caskID: String, name: String, addedAt: Date = Date()) {
        self.caskID = caskID
        self.name = name
        self.addedAt = addedAt
    }
}

public enum SoftwareUpdateError: LocalizedError {
    case homebrewNotInstalled
    case commandFailed(String, Int32, String)
    case invalidResponse(String)
    case invalidCaskID
    case unavailableStore(String)

    public var errorDescription: String? {
        switch self {
        case .homebrewNotInstalled:
            return "Homebrew was not found. App Store and vendor-managed apps can still be updated from their own update controls."
        case let .commandFailed(step, status, detail):
            return "\(step) failed (exit \(status)). \(detail)"
        case .invalidResponse(let message): return "Homebrew returned an unreadable response. \(message)"
        case .invalidCaskID: return "The Homebrew package identity is invalid. No command was run."
        case .unavailableStore(let message): return message
        }
    }
}
