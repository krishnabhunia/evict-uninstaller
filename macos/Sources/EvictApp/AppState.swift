#if os(macOS)
import SwiftUI
import EvictKit

/// Everything the windows share: the application list, the current uninstall plan and the settings.
@MainActor
final class AppState: ObservableObject {
    enum Page: String, CaseIterable, Identifiable {
        case applications = "Applications"
        case forceUninstall = "Force Uninstall"
        case startupItems = "Startup Items"
        case history = "History"
        case settings = "Settings"

        var id: String { rawValue }

        var icon: String {
            switch self {
            case .applications: return "square.grid.2x2"
            case .forceUninstall: return "hammer"
            case .startupItems: return "bolt.badge.clock"
            case .history: return "clock.arrow.circlepath"
            case .settings: return "gearshape"
            }
        }
    }

    // ── navigation ──
    @Published var page: Page = .applications

    // ── applications ──
    @Published private(set) var apps: [InstalledApp] = []
    @Published private(set) var isScanning = false
    @Published private(set) var scanProgress: Double = 0
    @Published private(set) var scanDetail: String = ""
    @Published var search: String = ""
    @Published var selection: Set<String> = []

    // ── uninstall ──
    @Published var plan: UninstallPlan?
    @Published var planSelection: Set<String> = []
    @Published var isPlanning = false
    @Published var removalResult: RemovalResult?
    @Published var isRemoving = false
    /// Several selected apps are reviewed and removed one after another (design AT3, choice M2).
    @Published private(set) var queue = UninstallQueue()

    // ── environment ──
    @Published var fullDiskAccess: Bool = FullDiskAccess.isGranted
    @Published var settings: EvictKit.Settings
    let settingsStore: SettingsStore
    let history = HistoryStore()
    private let planner = UninstallPlanner()
    private var casks: [String] = []

    init() {
        // The store must exist before `settings` can be read from it.
        let store = SettingsStore()
        settingsStore = store
        settings = store.current
    }

    var colorScheme: ColorScheme? {
        switch settings.appearance {
        case "Light": return .light
        case "Dark": return .dark
        default: return nil
        }
    }

    var visibleApps: [InstalledApp] {
        let query = search.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return apps }
        return apps.filter {
            $0.name.localizedCaseInsensitiveContains(query) ||
            ($0.bundleIdentifier ?? "").localizedCaseInsensitiveContains(query)
        }
    }

    var totalSize: Int64 { apps.reduce(0) { $0 + $1.sizeBytes } }

    func updateSettings(_ change: (inout EvictKit.Settings) -> Void) {
        settingsStore.update(change)
        settings = settingsStore.current
    }

    // ── scanning ──

    func refreshApplications() async {
        guard !isScanning else { return }
        isScanning = true
        scanProgress = 0
        scanDetail = "Looking through your application folders…"
        let includeSystem = settings.includeSystemApps

        let result: ([InstalledApp], [String]) = await Task.detached(priority: .userInitiated) {
            let casks = HomebrewService.installedCasks()
            let apps = AppInventory().scan(includeSystemApps: includeSystem)
            return (apps, casks)
        }.value

        apps = result.0
        casks = result.1
        isScanning = false
        scanProgress = 1
        scanDetail = ""
        fullDiskAccess = FullDiskAccess.isGranted
    }

    func app(withID id: String) -> InstalledApp? { apps.first { $0.id == id } }

    // ── uninstall flow ──

    func preparePlan(for app: InstalledApp) async {
        guard !isPlanning, !isRemoving else { return }
        isPlanning = true
        plan = nil
        removalResult = nil
        let planner = self.planner
        let casks = self.casks
        let options = settings
        let built = await Task.detached(priority: .userInitiated) { planner.plan(for: app, casks: casks, settings: options) }.value
        plan = built
        planSelection = built.initialSelection(settings: options)
        isPlanning = false
    }

    func prepareForcePlan(name: String, bundlePath: String?) async {
        guard !isPlanning, !isRemoving else { return }
        isPlanning = true
        plan = nil
        removalResult = nil
        let planner = self.planner
        let options = settings
        let built = await Task.detached(priority: .userInitiated) { planner.plan(forLeftoverName: name, bundlePath: bundlePath, settings: options) }.value
        plan = built
        planSelection = built.initialSelection(settings: options)
        isPlanning = false
    }

    /// Uninstalls one or more apps: each gets its own review sheet, one after another.
    func uninstall(_ picked: [InstalledApp]) async {
        guard !isPlanning, !isRemoving, plan == nil else { return }
        queue = UninstallQueue(picked)
        guard let first = queue.current else { return }
        await preparePlan(for: first)
    }

    /// Stops after the review that is open now; the remaining apps are left alone.
    func cancelQueue() {
        queue.clear()
    }

    func performRemoval() async {
        guard !isRemoving, !isPlanning, let plan else { return }
        isRemoving = true
        let planner = self.planner
        let selection = planSelection
        let history = self.history
        let result = await Task.detached(priority: .userInitiated) {
            planner.execute(plan: plan, selection: selection, history: history)
        }.value
        removalResult = result
        isRemoving = false
        await refreshApplications()
    }

    func dismissPlan() {
        guard !isRemoving else { return }
        // The sheet can report its dismissal more than once; only a real close moves the queue on.
        let hadPlan = plan != nil
        plan = nil
        planSelection = []
        removalResult = nil
        guard hadPlan, !queue.isEmpty else { queue.clear(); return }
        guard let next = queue.advance() else { queue.clear(); return }
        Task { @MainActor in
            // Let the closing sheet finish its animation before the next one opens.
            try? await Task.sleep(nanoseconds: 350_000_000)
            guard queue.current?.id == next.id else { return }
            await preparePlan(for: next)
        }
    }
}

#endif
