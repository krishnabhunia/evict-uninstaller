#if os(macOS)
import AppKit
import SwiftUI
import EvictKit

/// Shared by the updater page, dashboard and scheduled/manual checks. Installs are an explicit
/// separate action; checking for updates, including a scheduled check, never upgrades apps.
@MainActor
final class SoftwareUpdaterModel: ObservableObject {
    static let shared = SoftwareUpdaterModel()

    @Published private(set) var candidates: [SoftwareUpdateCandidate] = []
    @Published var selectedIDs: Set<String> = []
    @Published var query = ""
    @Published var showExcluded = false
    @Published private(set) var isChecking = false
    @Published private(set) var isUpdating = false
    @Published private(set) var lastChecked: Date?
    @Published private(set) var managedCaskCount = 0
    @Published private(set) var errorMessage: String?
    @Published private(set) var persistenceError: String?
    @Published private(set) var statusMessage = "Check for app updates, then choose what to install."
    @Published private(set) var activeCaskID: String?
    @Published private(set) var completedCount = 0
    @Published private(set) var queuedCount = 0
    @Published private(set) var stopRequested = false
    @Published private(set) var exclusions: [SoftwareUpdateExclusion] = []
    @Published private(set) var historyEntries: [SoftwareUpdateHistoryEntry] = []

    private var hasFreshScan = false
    private var service: HomebrewSoftwareUpdateService
    private var store: SoftwareUpdateStore

    init(service: HomebrewSoftwareUpdateService = .init(), store: SoftwareUpdateStore = .init()) {
        self.service = service
        self.store = store
        refreshSavedRecords()
    }

    var isBusy: Bool { isChecking || isUpdating }
    var hasHomebrew: Bool { service.brewPath != nil }
    var recordsURL: URL { store.fileURL }
    var canResetRecords: Bool { !store.isReadable && !isBusy }
    var availableCount: Int { candidates.filter { $0.blockedReason == nil && !isExcluded($0.id) }.count }
    var selectedCandidates: [SoftwareUpdateCandidate] { candidates.filter { selectedIDs.contains($0.id) && isSelectable($0) } }
    var visibleCandidates: [SoftwareUpdateCandidate] {
        let text = query.trimmingCharacters(in: .whitespacesAndNewlines)
        return candidates.filter {
            (showExcluded || !isExcluded($0.id)) && (text.isEmpty || $0.name.localizedCaseInsensitiveContains(text) || $0.caskID.localizedCaseInsensitiveContains(text))
        }
    }

    func isExcluded(_ id: String) -> Bool { exclusions.contains { $0.caskID == id } }

    func isSelectable(_ candidate: SoftwareUpdateCandidate) -> Bool {
        !isBusy && hasFreshScan && store.isReadable && candidate.blockedReason == nil && !isExcluded(candidate.id)
    }

    func selectAllVisible() {
        selectedIDs = Set(visibleCandidates.filter { isSelectable($0) }.map(\.id))
    }

    func selectNone() { selectedIDs = [] }

    func checkForUpdates() async {
        guard !isBusy else { return }
        if service.brewPath == nil { service = .init() }
        isChecking = true
        hasFreshScan = false
        selectedIDs = []
        errorMessage = nil
        statusMessage = "Refreshing the Homebrew catalogue and checking installed apps…"
        let provider = service
        do {
            let scan = try await Task.detached(priority: .utility) { try provider.checkForUpdates() }.value
            apply(scan)
            statusMessage = availableCount == 0
                ? "No selectable Homebrew updates were found. App Store and vendor-managed apps have their own update controls."
                : "\(availableCount) update\(availableCount == 1 ? "" : "s") available. Select the apps you want to update."
        } catch {
            errorMessage = error.localizedDescription
            statusMessage = "The check did not complete. Previous results cannot be installed until a check succeeds."
        }
        isChecking = false
        refreshSavedRecords()
    }

    func exclude(_ values: [SoftwareUpdateCandidate]) {
        guard !isBusy else { return }
        do {
            try store.exclude(values)
            selectedIDs.subtract(values.map(\.id))
            errorMessage = nil
        } catch { errorMessage = error.localizedDescription }
        refreshSavedRecords()
    }

    func restoreExclusions(_ ids: Set<String>) {
        guard !isBusy else { return }
        do { try store.include(ids); errorMessage = nil }
        catch { errorMessage = error.localizedDescription }
        refreshSavedRecords()
    }

    func stopAfterCurrentApp() { if isUpdating { stopRequested = true } }

    /// Called only with the snapshot displayed in the confirmation sheet. Each package is
    /// checked again inside the service before invoking Homebrew.
    func install(confirmed: [SoftwareUpdateCandidate]) async {
        guard !isBusy, hasFreshScan, store.isReadable, !confirmed.isEmpty else { return }
        guard Set(confirmed.map(\.id)).count == confirmed.count,
              confirmed.allSatisfy({ approved in candidates.contains(approved) && isSelectable(approved) }) else {
            errorMessage = "The selection changed. Check updates and review the versions again."
            return
        }
        isUpdating = true
        stopRequested = false
        errorMessage = nil
        completedCount = 0
        queuedCount = confirmed.count
        var outcomes: [SoftwareUpdateStatus] = []
        let provider = service
        for candidate in confirmed {
            if stopRequested { break }
            activeCaskID = candidate.id
            statusMessage = "Updating \(candidate.name) to \(candidate.availableVersion)…"
            let attempt: SoftwareUpdateHistoryEntry
            do { attempt = try store.begin(candidate) }
            catch { errorMessage = error.localizedDescription; break }
            refreshSavedRecords()

            let runningPaths = Set(NSWorkspace.shared.runningApplications.compactMap { $0.bundleURL?.resolvingSymlinksInPath().path })
            let result: SoftwareUpdateResult
            if candidate.appPaths.contains(where: { runningPaths.contains(URL(fileURLWithPath: $0).resolvingSymlinksInPath().path) }) {
                result = .init(status: .blocked, details: "This app is running. Quit it, check for updates again, and retry. Evict did not force it to close.")
            } else {
                result = await Task.detached(priority: .userInitiated) { provider.update(candidate) }.value
            }
            outcomes.append(result.status)
            completedCount += 1
            do { try store.finish(attempt.id, result: result) }
            catch { errorMessage = error.localizedDescription; refreshSavedRecords(); break }
            refreshSavedRecords()
        }
        activeCaskID = nil
        selectedIDs = []
        hasFreshScan = false
        // Re-read installed state even after errors; never keep displaying the assumed target
        // as the installed version simply because an installer returned exit code zero.
        do {
            let scan = try await Task.detached(priority: .utility) { try provider.checkForUpdates(refreshCatalog: false) }.value
            apply(scan)
        } catch {
            let detail = "The final installed-state check failed: \(error.localizedDescription)"
            errorMessage = [errorMessage, detail].compactMap { $0 }.joined(separator: "\n")
        }
        let verified = outcomes.filter { $0 == .updated }.count
        let current = outcomes.filter { $0 == .alreadyCurrent }.count
        let attention = outcomes.count - verified - current
        let remaining = confirmed.count - completedCount
        statusMessage = "\(verified) verified · \(current) already current · \(attention) need attention" + (remaining > 0 ? " · \(remaining) not started" : "") + ". See Update History for details."
        isUpdating = false
        refreshSavedRecords()
    }

    func prepareRetry(_ entry: SoftwareUpdateHistoryEntry) async {
        await checkForUpdates()
        guard !isBusy, hasFreshScan else { return }
        guard let candidate = candidates.first(where: { $0.id == entry.caskID }), isSelectable(candidate) else {
            statusMessage = "This app has no selectable update. It may already be current, excluded, pinned, or no longer installed."
            return
        }
        selectedIDs = [candidate.id]
        statusMessage = "Review \(candidate.name)'s current target version before choosing Update selected."
    }

    func clearHistory() {
        guard !isBusy else { return }
        do { try store.clearHistory(); errorMessage = nil }
        catch { errorMessage = error.localizedDescription }
        refreshSavedRecords()
    }

    func resetRecords() {
        guard !isBusy else { return }
        do { try store.resetRecords(); errorMessage = nil }
        catch { errorMessage = error.localizedDescription }
        selectedIDs = []
        refreshSavedRecords()
    }

    func reloadRecords() {
        guard !isBusy, !store.isReadable else { return }
        store = SoftwareUpdateStore(path: store.fileURL.path)
        refreshSavedRecords()
    }

    func refreshSavedRecords() {
        exclusions = store.exclusions
        historyEntries = store.history()
        persistenceError = store.lastError
    }

    func exportHistory(to url: URL) {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            encoder.dateEncodingStrategy = .iso8601
            try encoder.encode(historyEntries).write(to: url, options: .atomic)
            statusMessage = "Update history exported."
        } catch { errorMessage = "The export could not be saved: \(error.localizedDescription)" }
    }

    private func apply(_ scan: SoftwareUpdateScan) {
        candidates = scan.candidates
        managedCaskCount = scan.managedCaskCount
        lastChecked = scan.checkedAt
        hasFreshScan = true
    }
}
#endif
