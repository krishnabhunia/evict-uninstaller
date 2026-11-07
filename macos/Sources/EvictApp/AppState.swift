#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

/// Everything the windows share: the application list, the current uninstall plan and the settings.
@MainActor
final class AppState: ObservableObject {
    enum Page: String, CaseIterable, Identifiable {
        case dashboard = "Home"
        case applications = "Applications"
        case softwareUpdater = "Software Updater"
        case versionManager = "Evict Versions"
        case tools = "Cleanup & Tools"
        case forceUninstall = "Force Uninstall"
        case startupItems = "Startup Items"
        case history = "History"
        case settings = "Settings"

        var id: String { rawValue }

        var icon: String {
            switch self {
            case .dashboard: return "house"
            case .applications: return "square.grid.2x2"
            case .softwareUpdater: return "arrow.down.circle"
            case .versionManager: return "clock.arrow.2.circlepath"
            case .tools: return "sparkles"
            case .forceUninstall: return "hammer"
            case .startupItems: return "bolt.badge.clock"
            case .history: return "clock.arrow.circlepath"
            case .settings: return "gearshape"
            }
        }
    }

    // ── navigation ──
    @Published var page: Page = .dashboard
    @Published var showSearch = false
    @Published private(set) var startupComplete = false
    @Published private(set) var startupProgress = 0.05
    @Published private(set) var startupMessage = "Preparing Evict…"
    @Published private(set) var startupSlow = false
    @Published var operationError: String?
    @Published private(set) var isApplyingSchedule = false
    @Published private(set) var scheduleMessage: String?
    @Published private(set) var runningAppNames: [String] = []
    private var startupStarted = false

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
        operationError = store.lastError
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

    @discardableResult
    func updateSettings(_ change: (inout EvictKit.Settings) -> Void) -> Bool {
        let saved = settingsStore.update(change)
        settings = settingsStore.current
        if !saved { operationError = settingsStore.lastError ?? "Settings could not be saved." }
        return saved
    }

    func start() async {
        guard !startupStarted else { return }
        startupStarted = true
        let slowNotice = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 10_000_000_000)
            if !Task.isCancelled && !startupComplete { startupSlow = true }
        }
        defer { slowNotice.cancel() }
        startupMessage = "Loading your preferences and history"
        startupProgress = 0.15
        await Task.yield()
        startupMessage = "Finding installed applications"
        startupProgress = 0.35
        await refreshApplications()
        startupMessage = "Checking permissions and update recovery"
        startupProgress = 0.9
        await ReleaseManager.shared.reconcilePendingInstallation()
        fullDiskAccess = FullDiskAccess.isGranted
        startupProgress = 1
        startupMessage = "Ready"
        startupComplete = true
        if settings.checkForUpdates {
            Task { await ReleaseManager.shared.checkForUpdates() }
        }
    }

    func openWhileScanning() { startupComplete = true }

    func applySchedule(_ schedule: ScanSchedule, resetPreferences: Bool = false) async {
        guard !isApplyingSchedule else { return }
        guard !isRemoving, !isPlanning, !SoftwareUpdaterModel.shared.isUpdating, !ReleaseManager.shared.isBusy else {
            operationError = "Finish the current operation before changing the scan schedule."
            return
        }
        isApplyingSchedule = true
        defer { isApplyingSchedule = false }
        let executable = Bundle.main.executableURL?.path ?? ""
        let store = settingsStore
        let error: String? = await Task.detached(priority: .userInitiated) {
            do {
                try ScanScheduler().apply(schedule, executable: executable) { applied in
                    let saved = resetPreferences ? store.reset() : store.update({ $0.scanSchedule = applied })
                    guard saved else {
                        throw ScanScheduleError.registration(store.lastError ?? "Could not save the schedule.")
                    }
                }
                return nil
            } catch { return error.localizedDescription }
        }.value
        settings = store.current
        scheduleMessage = error ?? (schedule.frequency == .never ? "Scheduled scans are disabled." : "Schedule applied and confirmed by macOS.")
        if let error { operationError = error }
    }

    func resetSettings() async {
        await applySchedule(ScanSchedule(), resetPreferences: true)
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
        guard !SoftwareUpdaterModel.shared.isUpdating, !ReleaseManager.shared.isBusy, !isApplyingSchedule else {
            operationError = "Finish the current update or schedule change before starting an uninstall."
            return
        }
        isPlanning = true
        plan = nil
        removalResult = nil
        let planner = self.planner
        let casks = self.casks
        let options = settings
        let built = await Task.detached(priority: .userInitiated) { planner.plan(for: app, casks: casks, settings: options) }.value
        plan = built
        planSelection = built.initialSelection(settings: options)
        refreshRunningApplications(for: built.app)
        isPlanning = false
    }

    func prepareForcePlan(name: String, bundlePath: String?) async {
        guard !isPlanning, !isRemoving else { return }
        guard !SoftwareUpdaterModel.shared.isUpdating, !ReleaseManager.shared.isBusy, !isApplyingSchedule else {
            operationError = "Finish the current update or schedule change before starting an uninstall."
            return
        }
        isPlanning = true
        plan = nil
        removalResult = nil
        let planner = self.planner
        let options = settings
        let built = await Task.detached(priority: .userInitiated) { planner.plan(forLeftoverName: name, bundlePath: bundlePath, settings: options) }.value
        plan = built
        planSelection = built.initialSelection(settings: options)
        refreshRunningApplications(for: built.app)
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
        guard !SoftwareUpdaterModel.shared.isUpdating, !ReleaseManager.shared.isBusy, !isApplyingSchedule else {
            operationError = "Finish the current update or schedule change before removing files."
            return
        }
        refreshRunningApplications(for: plan.app)
        guard runningAppNames.isEmpty else {
            operationError = "Quit \(plan.app.name) before removing its application or data."
            return
        }
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

    private func runningApplications(for app: InstalledApp) -> [NSRunningApplication] {
        guard !app.bundlePath.isEmpty else { return [] }
        let path = URL(fileURLWithPath: app.bundlePath).resolvingSymlinksInPath().path
        return NSWorkspace.shared.runningApplications.filter {
            guard !$0.isTerminated else { return false }
            let bundle = $0.bundleURL?.resolvingSymlinksInPath().path ?? ""
            let executable = $0.executableURL?.resolvingSymlinksInPath().path ?? ""
            return bundle == path || bundle.hasPrefix(path + "/") || executable.hasPrefix(path + "/")
        }
    }

    func refreshRunningApplications(for app: InstalledApp) {
        runningAppNames = Array(Set(runningApplications(for: app).map { $0.localizedName ?? app.name })).sorted()
    }

    func quitPlannedApplication() async {
        guard let app = plan?.app else { return }
        let running = runningApplications(for: app)
        guard !running.contains(where: { $0.processIdentifier == ProcessInfo.processInfo.processIdentifier }) else {
            operationError = "Quit Evict and move it to Trash from Finder to uninstall Evict itself."
            return
        }
        for process in running { _ = process.terminate() }
        for _ in 0..<20 {
            refreshRunningApplications(for: app)
            if runningAppNames.isEmpty { return }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        operationError = "The application is still running. Save your work and quit it before continuing."
    }

    func dismissPlan() {
        guard !isRemoving else { return }
        // The sheet can report its dismissal more than once; only a real close moves the queue on.
        let hadPlan = plan != nil
        plan = nil
        planSelection = []
        removalResult = nil
        runningAppNames = []
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
