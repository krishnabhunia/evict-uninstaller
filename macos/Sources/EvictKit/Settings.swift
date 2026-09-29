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
    public var settingsVersion: Int = 1

    public init() {}
}

/// Loads and saves `Settings`; every change is written straight away.
public final class SettingsStore: @unchecked Sendable {
    private let path: String
    private let lock = NSLock()
    public private(set) var current: Settings

    public init(path: String = AppPaths.settingsFile) {
        self.path = path
        if let data = FileManager.default.contents(atPath: path),
           let decoded = try? JSONDecoder().decode(Settings.self, from: data) {
            current = decoded
        } else {
            current = Settings()
        }
    }

    public func update(_ change: (inout Settings) -> Void) {
        lock.lock()
        change(&current)
        let snapshot = current
        lock.unlock()
        save(snapshot)
    }

    public func reset() { update { $0 = Settings() } }

    private func save(_ settings: Settings) {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(settings) else { return }
        try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
    }
}
