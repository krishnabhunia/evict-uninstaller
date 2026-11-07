#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

@MainActor
final class ReleaseManager: ObservableObject {
    static let shared = ReleaseManager()

    @Published private(set) var releases: [MacRelease] = []
    @Published private(set) var preferences: MacReleasePreferences
    @Published private(set) var history: [MacReleaseHistoryEntry]
    @Published private(set) var recoveryCopies: [MacReleaseRecoveryCopy]
    @Published private(set) var isChecking = false
    @Published private(set) var isBusy = false
    @Published private(set) var status = "Check for native macOS releases when you are ready."
    @Published private(set) var errorMessage: String?
    @Published private(set) var lastChecked: Date?
    @Published private(set) var prepared: PreparedMacRelease?
    @Published var selectedTag: String?

    let runningVersion: String
    private let store: MacReleaseStore
    private let client = MacReleaseClient()
    private let bundledNotes: String
    private var operation: Task<Void, Never>?
    private var reconciled = false

    init(store: MacReleaseStore = MacReleaseStore()) {
        self.store = store
        let saved = store.snapshot
        preferences = saved.preferences
        history = saved.history
        recoveryCopies = saved.recoveryCopies
        runningVersion = Bundle.main.object(forInfoDictionaryKey: "EvictReleaseVersion") as? String ?? Version.current
        bundledNotes = Bundle.main.url(forResource: "release-notes", withExtension: "md")
            .flatMap { try? String(contentsOf: $0, encoding: .utf8) } ?? "Implementation notes are not included in this older build. Check for releases to load its published changes."
        errorMessage = store.lastError
    }

    var availableUpdate: MacRelease? { MacReleasePolicy.availableUpdate(releases, current: runningVersion, preferences: preferences) }
    var hasAvailableUpdate: Bool { availableUpdate != nil }
    var latestUpdateVersion: String? { availableUpdate?.version.rawValue }
    var visibleReleases: [MacRelease] { MacReleasePolicy.visible(releases, includeBeta: preferences.includeBeta, showAll: preferences.showAllBuilds) }
    var selectedRelease: MacRelease? { visibleReleases.first { $0.tag == selectedTag } }
    var currentReleaseNotes: String { releases.first(where: { $0.version.rawValue == runningVersion && !$0.notes.isEmpty })?.notes ?? bundledNotes }
    var latestStableTag: String? { MacReleasePolicy.sorted(releases).first(where: { !$0.isBeta })?.tag }
    var latestBetaTag: String? { MacReleasePolicy.sorted(releases).first(where: { $0.isBeta })?.tag }

    func checkForUpdates() async {
        guard !isChecking, !isBusy else { return }
        isChecking = true
        errorMessage = store.lastError
        status = "Checking GitHub's native macOS release catalog…"
        defer { isChecking = false }
        do {
            let catalog = try await client.fetchCatalog()
            try Task.checkCancellation()
            releases = catalog
            lastChecked = Date()
            ensureSelection()
            if let latest = availableUpdate {
                status = "Evict \(latest.version) is available for macOS. Choose a version to review its changes."
            } else if preferences.pinnedVersion != nil {
                status = "Your installed version is pinned. You can still choose a release manually."
            } else if catalog.isEmpty {
                status = "No compatible native macOS releases are published yet. Older combined Windows packages are not treated as native Mac updates."
            } else {
                status = "No newer release is available for your channel and skipped-version preferences."
            }
        } catch is CancellationError {
            status = "Update check cancelled."
        } catch {
            errorMessage = error.localizedDescription
            status = "The update check did not complete."
        }
    }

    func setIncludeBeta(_ enabled: Bool) { changePreferences { $0.includeBeta = enabled }; ensureSelection() }
    func setShowAllBuilds(_ enabled: Bool) { changePreferences { $0.showAllBuilds = enabled }; ensureSelection() }
    func pinInstalledVersion() { changePreferences { $0.pinnedVersion = runningVersion } }
    func unpin() { changePreferences { $0.pinnedVersion = nil } }
    func clearSkippedVersions() { changePreferences { $0.skippedTags = [] } }

    func skipSelectedVersion() {
        guard let selected = selectedRelease else { return }
        changePreferences { $0.skippedTags.append(selected.tag) }
    }

    func prepareVersion(_ release: MacRelease) {
        guard !isBusy, !isChecking else { return }
        guard visibleReleases.contains(release) else {
            errorMessage = "The selected release changed or is no longer enabled. Check the list and choose it again."
            return
        }
        isBusy = true
        errorMessage = nil
        status = "Downloading and verifying Evict \(release.version)…"
        let currentBundle = Bundle.main.bundleURL
        let currentVersion = runningVersion
        let store = self.store
        operation = Task { [weak self] in
            guard let self else { return }
            let temporary = FileManager.default.temporaryDirectory.appendingPathComponent("Evict-download-\(UUID().uuidString)", isDirectory: true)
            defer {
                try? FileManager.default.removeItem(at: temporary)
                self.isBusy = false
                self.operation = nil
                self.reloadSavedState()
            }
            do {
                let archive = try await self.client.download(release, into: temporary)
                try Task.checkCancellation()
                self.status = "Checking the native application and saving a recovery copy…"
                let work = Task.detached(priority: .userInitiated) {
                    try MacReleasePreparation.prepare(archive: archive, release: release, currentBundle: currentBundle,
                                                      currentVersion: currentVersion, store: store)
                }
                let ready = try await withTaskCancellationHandler(operation: { try await work.value }, onCancel: { work.cancel() })
                self.prepared = ready
                self.status = "Evict \(release.version) is ready. Confirm the Finder handoff to install it."
            } catch is CancellationError {
                self.status = "Preparation cancelled. Your installed application has not changed."
            } catch {
                self.errorMessage = error.localizedDescription
                self.status = "This version was not installed."
                self.recordFailure(operation: .install, toVersion: release.version.rawValue, tag: release.tag, error: error)
            }
        }
    }

    func prepareRecovery(_ copy: MacReleaseRecoveryCopy) {
        guard !isBusy, !isChecking else { return }
        isBusy = true
        errorMessage = nil
        status = "Verifying recovery copy for Evict \(copy.version)…"
        let currentBundle = Bundle.main.bundleURL, version = runningVersion, store = self.store
        operation = Task { [weak self] in
            guard let self else { return }
            defer { self.isBusy = false; self.operation = nil; self.reloadSavedState() }
            do {
                let work = Task.detached(priority: .userInitiated) {
                    try MacReleasePreparation.prepareRollback(copy, currentBundle: currentBundle, currentVersion: version, store: store)
                }
                let ready = try await withTaskCancellationHandler(operation: { try await work.value }, onCancel: { work.cancel() })
                self.prepared = ready
                self.status = "Recovery copy verified. Confirm the Finder handoff to restore Evict \(copy.version)."
            } catch is CancellationError {
                self.status = "Recovery preparation cancelled. Your installed application has not changed."
            } catch {
                self.errorMessage = error.localizedDescription
                self.status = "Recovery was stopped."
                self.recordFailure(operation: .rollback, toVersion: copy.version, tag: nil, error: error)
            }
        }
    }

    func cancelPreparation() { operation?.cancel() }

    func openPreparedInFinderAndQuit(whenIdle: @MainActor () -> Bool) async {
        guard !isBusy, let prepared else { return }
        guard whenIdle() else {
            errorMessage = "Finish the current uninstall or software update before replacing Evict."
            return
        }
        isBusy = true
        defer { isBusy = false }
        let entry = prepared.entry, store = self.store
        do {
            let ready = try await Task.detached(priority: .userInitiated) {
                try MacReleasePreparation.validatePrepared(entry, store: store)
            }.value
            guard whenIdle() else {
                throw MacReleaseError.file("Finish the current uninstall or software update before replacing Evict.")
            }
            let destination = URL(fileURLWithPath: entry.installationPath).deletingLastPathComponent()
            // Finder performs replacement and obtains any required administrator authorization.
            guard NSWorkspace.shared.open(destination) else { throw MacReleaseError.file("Finder could not open the installation folder.") }
            NSWorkspace.shared.activateFileViewerSelecting([ready.applicationURL])
            try store.update { state in
                if let index = state.history.firstIndex(where: { $0.id == entry.id }) {
                    state.history[index].status = .handedOff
                    state.history[index].detail = "Opened the verified application and destination in Finder. Waiting for replacement and a verified relaunch."
                }
            }
            reloadSavedState()
            // Validation and persistence are complete. Allow the confirmed Finder
            // handoff to quit, while normal quits still guard active preparation.
            isBusy = false
            NSApp.terminate(nil)
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func cancelPendingInstallation() {
        guard let prepared, !isBusy else { return }
        do {
            try store.update { state in
                if let index = state.history.firstIndex(where: { $0.id == prepared.id }) {
                    state.history[index].status = .cancelled
                    state.history[index].detail = "Installation cancelled by the user. The recovery copy remains available."
                }
            }
            self.prepared = nil
            reloadSavedState()
            status = "Pending installation cancelled."
        } catch { errorMessage = error.localizedDescription }
    }

    /// Startup reconciliation runs once per process and does not make a network request.
    func reconcilePendingInstallation() async {
        guard !reconciled else { return }
        reconciled = true
        let bundle = Bundle.main.bundleURL, version = runningVersion, store = self.store
        guard bundle.pathExtension.lowercased() == "app" else { return }
        do {
            let verified = try await Task.detached(priority: .utility) {
                guard store.snapshot.history.contains(where: { [.prepared, .handedOff].contains($0.status) }) else { return false }
                let digest = try MacReleasePreparation.validateNativeBundle(bundle, version: version)
                return try store.reconcile(version: version, bundlePath: bundle.standardizedFileURL.path, executableDigest: digest)
            }.value
            reloadSavedState()
            if verified { status = "Evict \(runningVersion) was installed and verified successfully." }
            if let pending = history.first(where: { [.prepared, .handedOff].contains($0.status) }) {
                do {
                    prepared = try await Task.detached(priority: .utility) {
                        try MacReleasePreparation.validatePrepared(pending, store: store)
                    }.value
                } catch {
                    errorMessage = "The previous prepared application is no longer usable: \(error.localizedDescription)"
                }
            }
        } catch { errorMessage = error.localizedDescription }
    }

    private func changePreferences(_ change: (inout MacReleasePreferences) -> Void) {
        do {
            try store.update { change(&$0.preferences) }
            reloadSavedState()
            errorMessage = nil
        } catch { errorMessage = error.localizedDescription }
    }

    private func reloadSavedState() {
        let saved = store.snapshot
        preferences = saved.preferences
        history = saved.history
        recoveryCopies = saved.recoveryCopies
    }

    private func recordFailure(operation: MacReleaseOperation, toVersion: String, tag: String?, error: Error) {
        let entry = MacReleaseHistoryEntry(operation: operation, fromVersion: runningVersion, toVersion: toVersion,
                                          releaseTag: tag, status: .failed,
                                          detail: String(error.localizedDescription.prefix(2000)),
                                          installationPath: Bundle.main.bundleURL.standardizedFileURL.path, executableDigest: "")
        try? store.update { $0.history.insert(entry, at: 0) }
    }

    private func ensureSelection() {
        if !visibleReleases.contains(where: { $0.tag == selectedTag }) { selectedTag = visibleReleases.first?.tag }
    }
}
#endif
