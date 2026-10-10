import Foundation

/// The columns of the Applications table (design AT1–AT4). The raw values are stored in the
/// user's defaults to remember the sort, so they must never change.
public enum AppColumn: String, CaseIterable, Sendable {
    case name, version, bundleID, source, size, installed, lastUsed, location

    public var title: String {
        switch self {
        case .name: return "Name"
        case .version: return "Version"
        case .bundleID: return "Bundle ID"
        case .source: return "Source"
        case .size: return "Size"
        case .installed: return "Installed"
        case .lastUsed: return "Last used"
        case .location: return "Location"
        }
    }

    /// Shown until the user hides it (right-click a header). Installed, Last used and Location start hidden.
    public var visibleByDefault: Bool {
        switch self {
        case .installed, .lastUsed, .location: return false
        default: return true
        }
    }
}

/// Non-optional values the table sorts and shows, so every column has something comparable.
public extension InstalledApp {
    var versionText: String { version ?? "" }
    var bundleIDText: String { bundleIdentifier ?? "" }
    var sourceText: String { source.rawValue }
    var installedSortDate: Date { installedDate ?? .distantPast }
    var lastUsedSortDate: Date { lastUsedDate ?? .distantPast }
}

public enum ApplicationsTable {

    /// Sorts the way a click on a column header does. Ties fall back to the name, so the order is stable.
    public static func sorted(_ apps: [InstalledApp], by column: AppColumn, ascending: Bool) -> [InstalledApp] {
        func byName(_ a: InstalledApp, _ b: InstalledApp) -> Bool {
            let order = a.name.localizedStandardCompare(b.name)
            return order == .orderedSame ? a.bundlePath < b.bundlePath : order == .orderedAscending
        }
        func compare(_ a: InstalledApp, _ b: InstalledApp) -> ComparisonResult {
            switch column {
            case .name: return a.name.localizedStandardCompare(b.name)
            case .version: return a.versionText.localizedStandardCompare(b.versionText)
            case .bundleID: return a.bundleIDText.localizedStandardCompare(b.bundleIDText)
            case .source: return a.sourceText.localizedStandardCompare(b.sourceText)
            case .location: return a.bundlePath.localizedStandardCompare(b.bundlePath)
            case .size: return a.sizeBytes == b.sizeBytes ? .orderedSame : (a.sizeBytes < b.sizeBytes ? .orderedAscending : .orderedDescending)
            case .installed: return a.installedSortDate.compare(b.installedSortDate)
            case .lastUsed: return a.lastUsedSortDate.compare(b.lastUsedSortDate)
            }
        }
        return apps.sorted { a, b in
            let order = compare(a, b)
            if order == .orderedSame { return byName(a, b) }
            return ascending ? order == .orderedAscending : order == .orderedDescending
        }
    }

    /// The bottom bar's text: "1 selected — Code · 913.9 MB", "3 selected — Claude, Code, Cursor · 2.74 GB",
    /// "12 selected — Acrobat, ChatGPT, Claude and 9 more · 9.1 GB". Names follow the table's order.
    public static func selectionSummary(_ apps: [InstalledApp], selected: Set<String>) -> String {
        let picked = apps.filter { selected.contains($0.id) }
        guard !picked.isEmpty else { return "Nothing selected" }
        let bytes = picked.reduce(Int64(0)) { $0 + $1.sizeBytes }
        let shown = picked.prefix(3).map(\.name).joined(separator: ", ")
        let more = picked.count > 3 ? " and \(picked.count - 3) more" : ""
        return "\(picked.count) selected — \(shown)\(more) · \(ByteFormat.string(bytes))"
    }

    /// Label for the bottom bar's button: "Uninstall…" for one app, "Uninstall 3 apps…" for several.
    public static func uninstallLabel(count: Int) -> String {
        count > 1 ? "Uninstall \(count) apps…" : "Uninstall…"
    }
}

/// Several selected apps are uninstalled one after another, each through its own review sheet
/// (design AT3, choice M2), so nothing is removed without the user ticking it.
public struct UninstallQueue: Sendable, Equatable {
    public private(set) var apps: [InstalledApp] = []
    public private(set) var position = 0   // index of the app whose review is (or will be) open

    public init() {}

    /// Removable apps only (apps that ship with macOS are skipped), in the order given, each once.
    public init(_ apps: [InstalledApp]) {
        var seen = Set<String>()
        self.apps = apps.filter { $0.source.isRemovable && seen.insert($0.id).inserted }
    }

    public var current: InstalledApp? { position < apps.count ? apps[position] : nil }
    public var isEmpty: Bool { current == nil }
    public var isBatch: Bool { apps.count > 1 }

    /// Moves on after the current review closed. Returns the next app, or nil when the queue is done.
    public mutating func advance() -> InstalledApp? {
        guard position < apps.count else { return nil }
        position += 1
        return current
    }

    /// "App 2 of 3" for the review sheet; empty for a single app.
    public var progressText: String {
        guard isBatch, current != nil else { return "" }
        return "App \(position + 1) of \(apps.count)"
    }

    public mutating func clear() { self = UninstallQueue() }
}
