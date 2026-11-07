import Foundation

/// User-visible options. Kept in one Codable struct so adding a field never breaks an old file.
public struct Settings: Codable, Sendable, Equatable {
    public var appearance: String = "System"        // System | Light | Dark
    public var includeSystemApps: Bool = false
    public var showLowConfidenceItems: Bool = true
    public var preselectLowConfidence: Bool = false
    public var confirmBeforeRemoving: Bool = true
    public var scanSystemLocations: Bool = true
    public var showMenuBarIcon: Bool = true
    public var checkForUpdates: Bool = true
    public var scanSchedule: ScanSchedule = ScanSchedule()
    public var settingsVersion: Int = 2

    public init() {}

    private enum CodingKeys: String, CodingKey {
        case appearance, includeSystemApps, showLowConfidenceItems, preselectLowConfidence,
             confirmBeforeRemoving, scanSystemLocations, showMenuBarIcon, checkForUpdates,
             scanSchedule, settingsVersion
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        appearance = try c.decodeIfPresent(String.self, forKey: .appearance) ?? "System"
        includeSystemApps = try c.decodeIfPresent(Bool.self, forKey: .includeSystemApps) ?? false
        showLowConfidenceItems = try c.decodeIfPresent(Bool.self, forKey: .showLowConfidenceItems) ?? true
        preselectLowConfidence = try c.decodeIfPresent(Bool.self, forKey: .preselectLowConfidence) ?? false
        confirmBeforeRemoving = try c.decodeIfPresent(Bool.self, forKey: .confirmBeforeRemoving) ?? true
        scanSystemLocations = try c.decodeIfPresent(Bool.self, forKey: .scanSystemLocations) ?? true
        showMenuBarIcon = try c.decodeIfPresent(Bool.self, forKey: .showMenuBarIcon) ?? true
        checkForUpdates = try c.decodeIfPresent(Bool.self, forKey: .checkForUpdates) ?? true
        scanSchedule = try c.decodeIfPresent(ScanSchedule.self, forKey: .scanSchedule) ?? ScanSchedule()
        settingsVersion = 2
    }
}

/// Loads and saves `Settings`; every change is written straight away.
public final class SettingsStore: @unchecked Sendable {
    private let path: String
    private let lock = NSLock()
    private var savedSettings: Settings
    public var current: Settings { lock.lock(); defer { lock.unlock() }; return savedSettings }
    private var savedError: String?
    public var lastError: String? { lock.lock(); defer { lock.unlock() }; return savedError }
    private var loadBlocked = false

    public init(path: String = AppPaths.settingsFile) {
        self.path = path
        savedSettings = Settings()
        if let data = FileManager.default.contents(atPath: path) {
            do { savedSettings = try JSONDecoder().decode(Settings.self, from: data) }
            catch {
                loadBlocked = true
                savedError = "Saved preferences could not be read. They are preserved; restore the file or explicitly reset settings. \(error.localizedDescription)"
            }
        } else if FileManager.default.fileExists(atPath: path) {
            loadBlocked = true
            savedError = "Saved preferences are unreadable and were preserved. Check file permissions before making changes."
        }
    }

    @discardableResult
    public func update(_ change: (inout Settings) -> Void) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !loadBlocked else { return false }
        var snapshot = savedSettings
        change(&snapshot)
        do {
            try save(snapshot)
            savedSettings = snapshot
            savedError = nil
            return true
        } catch {
            savedError = error.localizedDescription
            return false
        }
    }

    @discardableResult
    public func reset() -> Bool {
        lock.lock()
        defer { lock.unlock() }
        do {
            let defaults = Settings()
            try save(defaults)
            savedSettings = defaults
            loadBlocked = false
            savedError = nil
            return true
        } catch { savedError = error.localizedDescription; return false }
    }

    private func save(_ settings: Settings) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(settings)
        let url = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}
