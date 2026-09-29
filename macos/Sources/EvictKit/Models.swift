import Foundation

/// Where an installed application came from. Determines how it can be removed.
public enum AppSource: String, Codable, Sendable, CaseIterable {
    case appStore      = "App Store"
    case installerPkg  = "Installer package"
    case homebrewCask  = "Homebrew"
    case dragInstalled = "Drag-installed"
    case system        = "System"
    case unknown       = "Unknown"

    /// System apps ship with macOS and are protected by SIP – they are listed but never removable.
    public var isRemovable: Bool { self != .system }
}

/// One installed application.
public struct InstalledApp: Identifiable, Hashable, Codable, Sendable {
    public var id: String { bundlePath }
    public let bundlePath: String
    public let name: String
    public let bundleIdentifier: String?
    public let version: String?
    public let vendorName: String?
    public let source: AppSource
    public let sizeBytes: Int64
    public let installedDate: Date?
    public let lastUsedDate: Date?
    /// Receipt identifiers from `pkgutil` that installed this bundle, if any.
    public let receiptIdentifiers: [String]

    public init(bundlePath: String,
                name: String,
                bundleIdentifier: String?,
                version: String?,
                vendorName: String? = nil,
                source: AppSource,
                sizeBytes: Int64,
                installedDate: Date? = nil,
                lastUsedDate: Date? = nil,
                receiptIdentifiers: [String] = []) {
        self.bundlePath = bundlePath
        self.name = name
        self.bundleIdentifier = bundleIdentifier
        self.version = version
        self.vendorName = vendorName
        self.source = source
        self.sizeBytes = sizeBytes
        self.installedDate = installedDate
        self.lastUsedDate = lastUsedDate
        self.receiptIdentifiers = receiptIdentifiers
    }

    /// `com.vendor.app` → `com.vendor`; used to find files left by helpers of the same vendor.
    public var bundleIdentifierPrefix: String? {
        guard let id = bundleIdentifier else { return nil }
        let parts = id.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        return parts.prefix(2).joined(separator: ".")
    }
}

/// How sure Evict is that a file belongs to the application being removed.
public enum Confidence: Int, Codable, Sendable, Comparable, CaseIterable {
    case low = 0, medium = 1, high = 2

    public static func < (a: Confidence, b: Confidence) -> Bool { a.rawValue < b.rawValue }

    public var label: String {
        switch self {
        case .high: return "High"
        case .medium: return "Medium"
        case .low: return "Low"
        }
    }
}

/// What kind of leftover an item is – drives the grouping in the review step.
public enum LeftoverKind: String, Codable, Sendable, CaseIterable {
    case appBundle        = "Application"
    case supportFiles     = "Support files"
    case preferences      = "Preferences"
    case caches           = "Caches"
    case containers       = "Containers"
    case savedState       = "Saved state"
    case logs             = "Logs"
    case launchItem       = "Startup item"
    case privilegedHelper = "Helper tool"
    case plugin           = "Plug-in"
    case receipt          = "Installer receipt"
    case binary           = "Command-line link"

    /// Items outside the user's home need an administrator to remove.
    public var isSystemScope: Bool { self == .privilegedHelper }
}

/// A single file, folder or receipt that can be removed.
public struct LeftoverItem: Identifiable, Hashable, Codable, Sendable {
    public var id: String { path }
    public let path: String
    public let kind: LeftoverKind
    public let confidence: Confidence
    public let sizeBytes: Int64
    /// Plain-English reason shown in the review step, e.g. "Named after the app's bundle ID".
    public let reason: String
    /// A `pkgutil` receipt identifier rather than a file on disk.
    public let receiptIdentifier: String?
    public let needsAdmin: Bool

    public init(path: String,
                kind: LeftoverKind,
                confidence: Confidence,
                sizeBytes: Int64,
                reason: String,
                receiptIdentifier: String? = nil,
                needsAdmin: Bool = false) {
        self.path = path
        self.kind = kind
        self.confidence = confidence
        self.sizeBytes = sizeBytes
        self.reason = reason
        self.receiptIdentifier = receiptIdentifier
        self.needsAdmin = needsAdmin
    }

    public var displayName: String { (path as NSString).lastPathComponent }
}

/// Outcome of one removal run.
public struct RemovalResult: Codable, Sendable {
    public var trashed: [String] = []
    public var failed: [(path: String, error: String)] = []
    public var bytesFreed: Int64 = 0
    /// Paths that were still on disk when Evict re-checked after trashing them.
    public var stillPresent: [String] = []
    public var needsAdminCount: Int = 0

    public init() {}

    public var succeededCount: Int { trashed.count - stillPresent.count }

    // Tuples are not Codable, so the failures are stored as a flat pair list.
    private enum CodingKeys: String, CodingKey { case trashed, failedPaths, failedErrors, bytesFreed, stillPresent, needsAdminCount }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        trashed = try c.decode([String].self, forKey: .trashed)
        let paths = try c.decode([String].self, forKey: .failedPaths)
        let errors = try c.decode([String].self, forKey: .failedErrors)
        failed = zip(paths, errors).map { (path: $0, error: $1) }
        bytesFreed = try c.decode(Int64.self, forKey: .bytesFreed)
        stillPresent = try c.decode([String].self, forKey: .stillPresent)
        needsAdminCount = try c.decode(Int.self, forKey: .needsAdminCount)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(trashed, forKey: .trashed)
        try c.encode(failed.map(\.path), forKey: .failedPaths)
        try c.encode(failed.map(\.error), forKey: .failedErrors)
        try c.encode(bytesFreed, forKey: .bytesFreed)
        try c.encode(stillPresent, forKey: .stillPresent)
        try c.encode(needsAdminCount, forKey: .needsAdminCount)
    }
}

/// One line in the History page.
public struct HistoryEntry: Identifiable, Codable, Sendable {
    public var id: UUID = UUID()
    public let date: Date
    public let appName: String
    public let bundleIdentifier: String?
    public let itemsRemoved: Int
    public let bytesFreed: Int64
    public let failures: Int

    public init(date: Date = Date(), appName: String, bundleIdentifier: String?, itemsRemoved: Int, bytesFreed: Int64, failures: Int) {
        self.date = date
        self.appName = appName
        self.bundleIdentifier = bundleIdentifier
        self.itemsRemoved = itemsRemoved
        self.bytesFreed = bytesFreed
        self.failures = failures
    }
}

/// A launchd job or login item.
public struct StartupItem: Identifiable, Hashable, Codable, Sendable {
    public var id: String { path }
    public let path: String
    public let label: String
    /// The executable the job runs, if it could be read from the plist.
    public let program: String?
    public let isDaemon: Bool
    public let isSystemScope: Bool
    public let runsAtLoad: Bool
    /// True when the program the job points at no longer exists.
    public let isOrphan: Bool

    public init(path: String, label: String, program: String?, isDaemon: Bool, isSystemScope: Bool, runsAtLoad: Bool, isOrphan: Bool) {
        self.path = path
        self.label = label
        self.program = program
        self.isDaemon = isDaemon
        self.isSystemScope = isSystemScope
        self.runsAtLoad = runsAtLoad
        self.isOrphan = isOrphan
    }
}
