#if os(macOS)
import AppKit
import SwiftUI
import UniformTypeIdentifiers
import EvictKit

enum MacMaintenanceTab: String, CaseIterable, Identifiable {
    case cleanup = "Cleanup", extensions = "Browser extensions", monitor = "Install Monitor", security = "Security"
    var id: String { rawValue }
}

@MainActor
final class MacMaintenanceModel: ObservableObject {
    static let shared = MacMaintenanceModel()

    @Published var tab: MacMaintenanceTab = .cleanup
    @Published private(set) var cleanup: MacCleanupScan?
    @Published private(set) var extensions: [MacBrowserExtension] = []
    @Published private(set) var startupItems: [StartupItem] = []
    @Published private(set) var security = MacSecuritySnapshot()
    @Published private(set) var isScanning = false
    @Published private(set) var isCleaning = false
    @Published private(set) var isRecordingSnapshot = false
    @Published private(set) var message: String?
    @Published private(set) var warnings: [String] = []
    @Published private(set) var error: String?
    @Published private(set) var lastRemoval: RemovalResult?
    @Published var selection = Set<String>()
    @Published private(set) var recording: MacInstallSnapshot?
    @Published private(set) var sessions: [MacInstallSession] = []
    private let monitorStore = MacInstallMonitorStore()

    var isBusy: Bool { isScanning || isCleaning || isRecordingSnapshot }
    var selectedCandidates: [MacCleanupCandidate] { cleanup?.candidates.filter { selection.contains($0.id) } ?? [] }
    var selectedBytes: Int64 { selectedCandidates.reduce(0) { $0 + $1.sizeBytes } }
    var orphanCount: Int { startupItems.filter(\.isOrphan).count }

    init() {
        recording = monitorStore.recording
        sessions = monitorStore.sessions
        error = monitorStore.lastError
    }

    func scan() async {
        guard !isBusy else { return }
        isScanning = true
        let result = await Task.detached(priority: .utility) {
            (MacCleanupScanner().scan(), MacBrowserExtensions.scan(), LaunchItems().scan(), MacSecuritySnapshot.read())
        }.value
        cleanup = result.0
        extensions = result.1.extensions
        startupItems = result.2
        security = result.3
        warnings = result.0.warnings + result.1.warnings
        selection.formIntersection(result.0.candidates.map(\.id))
        isScanning = false
    }

    func trashSelected(history: HistoryStore) async {
        guard !isBusy else { return }
        let selected = selectedCandidates
        guard !selected.isEmpty else { return }
        isCleaning = true
        error = nil
        let result = await Task.detached(priority: .userInitiated) {
            let result = MacCleanupScanner().moveToTrash(selected)
            history.append(HistoryEntry(appName: "Cache and log cleanup", bundleIdentifier: nil,
                itemsRemoved: result.succeededCount, bytesFreed: result.bytesFreed, failures: result.failed.count))
            return result
        }.value
        lastRemoval = result
        selection.subtract(result.trashed)
        message = "Moved \(result.succeededCount) items (\(ByteFormat.string(result.bytesFreed))) to the Trash."
        if !result.failed.isEmpty { error = "\(result.failed.count) items could not be moved. Open Results for details." }
        if let historyError = history.lastError {
            error = "Cleanup finished, but its history could not be saved: \(historyError)"
        }
        isCleaning = false
        await scan()
    }

    func startRecording() async {
        guard !isBusy, recording == nil else { return }
        isRecordingSnapshot = true
        let snapshot = await Task.detached(priority: .userInitiated) { MacInstallMonitor.capture() }.value
        do {
            try monitorStore.begin(snapshot)
            recording = monitorStore.recording
            message = "Recording started. Install or update an app, then select Finish recording."
            error = nil
        } catch { self.error = error.localizedDescription }
        isRecordingSnapshot = false
    }

    func finishRecording() async {
        guard !isBusy, recording != nil else { return }
        isRecordingSnapshot = true
        let snapshot = await Task.detached(priority: .userInitiated) { MacInstallMonitor.capture() }.value
        do {
            let session = try monitorStore.finish(snapshot)
            recording = monitorStore.recording
            sessions = monitorStore.sessions
            message = "Recording saved: \(session.changes.count) observed changes."
            error = nil
        } catch { self.error = error.localizedDescription }
        isRecordingSnapshot = false
    }

    func cancelRecording() {
        guard !isBusy else { return }
        do {
            try monitorStore.cancelRecording()
            recording = nil
            message = "Recording cancelled. Earlier saved recordings are unchanged."
            error = nil
        } catch { self.error = error.localizedDescription }
    }

    func manage(_ browser: MacBrowser) {
        guard let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: browser.bundleIdentifier) else {
            error = "\(browser.rawValue) is not installed. Its profile may belong to a removed application."
            return
        }
        NSWorkspace.shared.open([browser.managementURL], withApplicationAt: app,
                                configuration: NSWorkspace.OpenConfiguration()) { _, failure in
            if let failure { Task { @MainActor in self.error = "Could not open \(browser.rawValue): \(failure.localizedDescription)" } }
        }
    }

    func export(_ session: MacInstallSession) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "Evict-Install-Recording-\(session.id.uuidString.prefix(8)).json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let encoder = JSONEncoder()
            encoder.dateEncodingStrategy = .iso8601
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(session).write(to: url, options: .atomic)
            message = "Recording exported."
        } catch { self.error = error.localizedDescription }
    }

    func openSettings(_ address: String) {
        guard let url = URL(string: address), NSWorkspace.shared.open(url) else {
            error = "Could not open System Settings. Open it from the Apple menu."
            return
        }
    }
}

struct ToolsView: View {
    @EnvironmentObject private var state: AppState
    @ObservedObject private var model = MacMaintenanceModel.shared
    @ObservedObject private var updater = SoftwareUpdaterModel.shared
    @ObservedObject private var releases = ReleaseManager.shared
    @State private var confirmCleanup = false
    @State private var confirmCancelRecording = false
    @State private var showResults = false
    @State private var extensionSearch = ""

    private var blocked: Bool { model.isBusy || state.isRemoving || state.isPlanning || state.isApplyingSchedule || updater.isUpdating || releases.isBusy }

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Clean & tools", subtitle: "Review what changes on your Mac, with every cleanup going to the Trash") {
                Button("Scan", systemImage: "arrow.clockwise") { Task { await model.scan() } }.disabled(blocked)
            }
            Picker("Tool", selection: $model.tab) {
                ForEach(MacMaintenanceTab.allCases) { Text($0.rawValue).tag($0) }
            }
            .pickerStyle(.segmented).padding(.horizontal, 20).padding(.bottom, 12)
            if model.isScanning { ProgressView().controlSize(.small).padding(5) }
            if let error = model.error {
                Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.orange)
                    .font(.callout).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 20).padding(.bottom, 8)
            } else if let message = model.message {
                Label(message, systemImage: "info.circle").foregroundStyle(.secondary)
                    .font(.callout).frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 20).padding(.bottom, 8)
            }
            if !model.warnings.isEmpty {
                DisclosureGroup("\(model.warnings.count) scan notices") {
                    ForEach(model.warnings, id: \.self) { Text($0).font(.caption).frame(maxWidth: .infinity, alignment: .leading) }
                }.padding(.horizontal, 20).padding(.bottom, 8)
            }
            Group {
                switch model.tab {
                case .cleanup: cleanupPage
                case .extensions: extensionPage
                case .monitor: monitorPage
                case .security: securityPage
                }
            }.frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .task { if model.cleanup == nil { await model.scan() } }
        .alert("Move \(model.selectedCandidates.count) reviewed items to the Trash?", isPresented: $confirmCleanup) {
            Button("Cancel", role: .cancel) { }
            Button("Move to Trash", role: .destructive) {
                guard !blocked else { return }
                Task { @MainActor in
                    guard !blocked else { return }
                    state.isRemoving = true
                    defer { state.isRemoving = false }
                    await model.trashSelected(history: state.history)
                    showResults = true
                }
            }
        } message: {
            Text("Close the applications that own these caches or logs first. You can restore moved items from the Trash. Space is reclaimed only after you empty it.")
        }
        .alert("Cancel this recording?", isPresented: $confirmCancelRecording) {
            Button("Keep recording", role: .cancel) { }
            Button("Cancel recording", role: .destructive) { model.cancelRecording() }
        } message: { Text("This discards the current baseline. Saved earlier recordings remain available.") }
        .sheet(isPresented: $showResults) { CleanupResultsView(result: model.lastRemoval) }
    }

    private var cleanupPage: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                Button("Select All") { model.selection = Set(model.cleanup?.candidates.map(\.id) ?? []) }.disabled(blocked)
                Button("Select None") { model.selection = [] }.disabled(blocked)
                Spacer()
                Text("\(model.selectedCandidates.count) selected · \(ByteFormat.string(model.selectedBytes))")
                    .font(.callout).foregroundStyle(.secondary)
                if model.lastRemoval != nil { Button("Results") { showResults = true } }
                Button("Move to Trash", systemImage: "trash") { confirmCleanup = true }
                    .buttonStyle(.borderedProminent).disabled(blocked || model.selectedCandidates.isEmpty)
            }
            .padding(.horizontal, 20).padding(.vertical, 10)
            if model.cleanup?.candidates.isEmpty != false {
                EmptyStateView(icon: "sparkles", title: model.cleanup == nil ? "Ready to scan" : "No cleanup candidates found",
                               message: "Evict looks in your Library caches and logs. Shared roots, Apple caches and symbolic links are excluded.")
            } else {
                List {
                    ForEach(MacCleanupCategory.allCases, id: \.self) { category in
                        let candidates = model.cleanup?.candidates.filter { $0.category == category } ?? []
                        if !candidates.isEmpty {
                            Section(category.rawValue) {
                                ForEach(candidates) { item in cleanupRow(item) }
                            }
                        }
                    }
                }.listStyle(.inset)
            }
            Divider()
            HStack {
                Button("Find app leftovers") { state.page = .forceUninstall }
                Button("Manage startup items") { state.page = .startupItems }
                Spacer()
                Button("Open Trash") { NSWorkspace.shared.open(URL(fileURLWithPath: AppPaths.home + "/.Trash")) }
                Button("Storage settings") { model.openSettings("x-apple.systempreferences:com.apple.settings.Storage") }
            }.padding(16)
        }
    }

    private func cleanupRow(_ item: MacCleanupCandidate) -> some View {
        HStack(spacing: 10) {
            Toggle(item.name, isOn: Binding(get: { model.selection.contains(item.id) }, set: { selected in
                if selected { model.selection.insert(item.id) } else { model.selection.remove(item.id) }
            })).labelsHidden().disabled(blocked).accessibilityLabel("Select " + item.name)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.name).fontWeight(.medium)
                Text(item.reviewNote).font(.caption).foregroundStyle(.secondary)
                Text(item.path).font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            Text((item.sizeIsEstimate ? "At least " : "") + ByteFormat.string(item.sizeBytes))
                .font(.callout.monospacedDigit()).foregroundStyle(.secondary)
            Button { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.path)]) } label: {
                Image(systemName: "folder")
            }.help("Show in Finder")
        }.padding(.vertical, 4)
    }

    private var extensionPage: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Inspect permissions here. Enable or remove an extension in its browser, which keeps profiles and sync consistent.")
                .font(.callout).foregroundStyle(.secondary).padding(.horizontal, 20)
            TextField("Search extensions, browsers or profiles", text: $extensionSearch)
                .textFieldStyle(.roundedBorder).padding(.horizontal, 20)
            if model.extensions.isEmpty {
                EmptyStateView(icon: "puzzlepiece.extension", title: "No extensions found",
                               message: "Evict scans local Chrome, Edge, Brave, Vivaldi, Opera and Firefox profiles. Safari extensions are managed in Safari Settings.")
            } else {
                List {
                    ForEach(model.extensions.filter {
                        extensionSearch.isEmpty || ($0.name + " " + $0.browser.rawValue + " " + $0.profile).localizedCaseInsensitiveContains(extensionSearch)
                    }) { item in
                        DisclosureGroup {
                            VStack(alignment: .leading, spacing: 7) {
                                Text(item.identifier).font(.caption.monospaced()).textSelection(.enabled)
                                Text(item.permissions.isEmpty ? "No permissions were declared in the local manifest." : item.permissions.joined(separator: ", "))
                                    .font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                                Text(item.isActive.map { $0 ? "Enabled in Firefox" : "Disabled in Firefox" } ?? "Open the browser to check whether this extension is enabled.")
                                    .font(.caption).foregroundStyle(.secondary)
                                HStack {
                                    Button("Manage in \(item.browser.rawValue)") { model.manage(item.browser) }
                                    Button("Show profile file") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: item.path)]) }
                                }
                            }.padding(.vertical, 8)
                        } label: {
                            HStack {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(item.name).fontWeight(.medium)
                                    Text("\(item.browser.rawValue) · \(item.profile) · v\(item.version)").font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                if item.hasBroadPermissions { Chip(text: "Broad permissions", tint: .orange) }
                            }.padding(.vertical, 5)
                        }
                    }
                }.listStyle(.inset)
            }
        }
    }

    private var monitorPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                Text("Start a recording, install or update an application, then finish to compare the before and after snapshots.")
                    .font(.headline)
                Text("Records application bundles and the first two levels of support, preference and launch-item folders. Concurrent app activity may also appear. A change is not proof of ownership, so recorded paths are never automatically removed.")
                    .font(.callout).foregroundStyle(.secondary)
                HStack(spacing: 10) {
                    if let recording = model.recording {
                        Label("Recording since \(recording.date.formatted(date: .abbreviated, time: .shortened))", systemImage: "record.circle")
                            .foregroundStyle(EvictTheme.accent)
                        Spacer()
                        Button("Cancel recording") { confirmCancelRecording = true }.disabled(blocked)
                        Button("Finish recording") { Task { await model.finishRecording() } }
                            .buttonStyle(.borderedProminent).disabled(blocked)
                    } else {
                        Button("Start recording", systemImage: "record.circle") { Task { await model.startRecording() } }
                            .buttonStyle(.borderedProminent).disabled(blocked)
                        Text("A recording survives closing and reopening Evict.").font(.callout).foregroundStyle(.secondary)
                    }
                }
                if model.isRecordingSnapshot { ProgressView("Reading the filesystem snapshot…").controlSize(.small) }
                if model.sessions.isEmpty {
                    Text("Saved recordings will appear here.").foregroundStyle(.secondary).padding(.vertical, 35)
                } else {
                    ForEach(model.sessions) { session in recordingCard(session) }
                }
            }.padding(20).frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func recordingCard(_ session: MacInstallSession) -> some View {
        DisclosureGroup {
            VStack(alignment: .leading, spacing: 8) {
                if session.incomplete {
                    Label("Partial recording: some locations were unreadable or the entry limit was reached.", systemImage: "exclamationmark.triangle")
                        .font(.callout).foregroundStyle(.orange)
                    ForEach(session.warnings, id: \.self) { Text($0).font(.caption).foregroundStyle(.secondary) }
                }
                ForEach(Array(session.changes.prefix(120))) { change in
                    HStack(alignment: .top) {
                        Chip(text: change.kind.rawValue, tint: change.kind == .added ? .green : .secondary)
                        VStack(alignment: .leading, spacing: 3) {
                            Text(change.path).font(.caption).textSelection(.enabled)
                            if let after = change.afterVersion {
                                Text("\(change.beforeVersion ?? "New") → \(after)").font(.caption2).foregroundStyle(.secondary)
                            }
                        }
                        Spacer()
                        if change.kind != .removed {
                            Button("Show") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: change.path)]) }
                        }
                    }
                }
                if session.changes.count > 120 { Text("Showing 120 changes. Export the complete recording to inspect all \(session.changes.count).") }
                Button("Export recording…", systemImage: "square.and.arrow.up") { model.export(session) }
            }.padding(.top, 12)
        } label: {
            HStack {
                Text(session.finished.formatted(date: .abbreviated, time: .shortened)).fontWeight(.medium)
                Spacer()
                Text("\(session.changes.count) changes").foregroundStyle(.secondary)
            }
        }
        .padding(15).background(EvictTheme.panel, in: RoundedRectangle(cornerRadius: 12))
    }

    private var securityPage: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Text("Native protection status").font(.title3).bold()
                Text("These are macOS configuration checks. Privacy permissions, notifications and operating system updates remain managed by macOS.")
                    .font(.callout).foregroundStyle(.secondary)
                securityRow("Gatekeeper", value: model.security.gatekeeper, detail: "App execution assessment")
                securityRow("FileVault", value: model.security.fileVault, detail: "Startup-disk encryption")
                securityRow("System Integrity Protection", value: model.security.systemIntegrity, detail: "Protection for system-owned files")
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 230))], spacing: 14) {
                    MaintenanceActionCard(title: "Privacy & Security", detail: "Review camera, microphone, location and app permissions.",
                                          icon: "hand.raised", button: "Open settings") {
                        model.openSettings("x-apple.systempreferences:com.apple.preference.security")
                    }
                    MaintenanceActionCard(title: "Notifications", detail: "Choose which applications may notify you.",
                                          icon: "bell", button: "Open settings") {
                        model.openSettings("x-apple.systempreferences:com.apple.Notifications-Settings.extension")
                    }
                    MaintenanceActionCard(title: "Software Update", detail: "Check for macOS updates using Apple’s updater.",
                                          icon: "arrow.triangle.2.circlepath", button: "Open settings") {
                        model.openSettings("x-apple.systempreferences:com.apple.Software-Update-Settings.extension")
                    }
                    MaintenanceActionCard(title: "Time Machine", detail: "Review your backup configuration before changing installed applications.",
                                          icon: "clock.arrow.circlepath", button: "Open settings") {
                        model.openSettings("x-apple.systempreferences:com.apple.Time-Machine-Settings.extension")
                    }
                }
            }.padding(20)
        }
    }

    private func securityRow(_ name: String, value: MacSecurityValue, detail: String) -> some View {
        HStack {
            VStack(alignment: .leading, spacing: 3) {
                Text(name).fontWeight(.medium)
                Text(detail).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Chip(text: value.rawValue, tint: value == .enabled ? .green : value == .disabled ? .orange : .secondary)
        }.padding(14).background(EvictTheme.panel, in: RoundedRectangle(cornerRadius: 10))
    }
}

private struct CleanupResultsView: View {
    let result: RemovalResult?
    @Environment(\.dismiss) private var dismiss
    var body: some View {
        VStack(alignment: .leading, spacing: 15) {
            HStack {
                Text("Cleanup results").font(.title2).bold()
                Spacer()
                Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            if let result {
                Text("\(result.succeededCount) items moved to the Trash · \(ByteFormat.string(result.bytesFreed)) moved")
                    .font(.headline)
                Text("Disk space is reclaimed only after you empty the Trash. Restore items from the Trash if needed.")
                    .font(.callout).foregroundStyle(.secondary)
                List {
                    ForEach(result.trashed, id: \.self) { path in Label(path, systemImage: "checkmark.circle").font(.caption) }
                    ForEach(result.failed.indices, id: \.self) { index in
                        VStack(alignment: .leading, spacing: 3) {
                            Text(result.failed[index].path).font(.caption).textSelection(.enabled)
                            Text(result.failed[index].error).font(.caption).foregroundStyle(.orange)
                        }
                    }
                }
            }
        }.padding(22).frame(width: 680, height: 480)
    }
}
#endif
