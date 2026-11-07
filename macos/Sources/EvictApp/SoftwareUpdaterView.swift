#if os(macOS)
import SwiftUI
import AppKit
import UniformTypeIdentifiers
import EvictKit

struct SoftwareUpdaterView: View {
    @EnvironmentObject private var state: AppState
    @ObservedObject private var model = SoftwareUpdaterModel.shared
    @ObservedObject private var releases = ReleaseManager.shared
    @State private var pane = Pane.updates
    @State private var showingExclusions = false
    @State private var showingOtherApps = false
    @State private var confirmingUpdates = false
    @State private var confirmingClear = false
    @State private var confirmingReset = false
    @State private var approved: [SoftwareUpdateCandidate] = []
    @State private var candidateDetails: SoftwareUpdateCandidate?
    @State private var historyDetails: SoftwareUpdateHistoryEntry?
    @State private var historyFilter: SoftwareUpdateStatus?

    private enum Pane: String, CaseIterable { case updates = "Available updates", history = "Update History" }
    private var otherOperationInProgress: Bool { state.isRemoving || state.isPlanning || state.isApplyingSchedule || releases.isBusy }

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Software Updater", subtitle: subtitle) {
                Button {
                    Task { await model.checkForUpdates() }
                } label: {
                    Label(model.isChecking ? "Checking…" : "Check for updates", systemImage: "arrow.clockwise")
                }
                .disabled(model.isBusy)
            }
            HStack(spacing: 12) {
                Picker("View", selection: $pane) {
                    ForEach(Pane.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                }
                .pickerStyle(.segmented).frame(maxWidth: 340)
                Spacer()
                Button("Exclusions (\(model.exclusions.count))") { showingExclusions = true }.disabled(model.isBusy)
                Button("Other apps…") { showingOtherApps = true }
            }
            .padding(.horizontal, 20).padding(.bottom, 12)

            if let error = model.errorMessage { notice(error, icon: "exclamationmark.triangle.fill", color: .orange) }
            if let error = model.persistenceError {
                notice(error, icon: "externaldrive.badge.exclamationmark", color: .orange)
            }
            HStack(spacing: 8) {
                if model.isBusy { ProgressView().controlSize(.small) }
                Text(model.statusMessage).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                Spacer(minLength: 10)
                if model.isUpdating {
                    Button(model.stopRequested ? "Stopping after this app…" : "Stop after current app") { model.stopAfterCurrentApp() }
                        .disabled(model.stopRequested)
                }
            }
            .padding(.horizontal, 20).padding(.bottom, 10)
            if model.isUpdating {
                ProgressView(value: Double(model.completedCount), total: Double(max(1, model.queuedCount)))
                    .padding(.horizontal, 20).padding(.bottom, 8)
            }
            Divider()
            switch pane {
            case .updates: updatesPane
            case .history: historyPane
            }
        }
        .task {
            model.refreshSavedRecords()
            if model.lastChecked == nil { await model.checkForUpdates() }
        }
        .onChange(of: model.isUpdating) { updating in
            if !updating { Task { await state.refreshApplications() } }
        }
        .sheet(isPresented: $showingExclusions) { exclusionsSheet }
        .sheet(isPresented: $showingOtherApps) { otherAppsSheet }
        .sheet(item: $candidateDetails) { candidate in candidateSheet(candidate) }
        .sheet(item: $historyDetails) { entry in historySheet(entry) }
        .sheet(isPresented: $confirmingUpdates) { confirmationSheet }
        .confirmationDialog("Clear the last seven days of update history?", isPresented: $confirmingClear) {
            Button("Clear update history", role: .destructive) { model.clearHistory() }
        } message: {
            Text("Saved exclusions and installed apps are kept.")
        }
        .confirmationDialog("Reset saved update records?", isPresented: $confirmingReset) {
            Button("Back up and reset records", role: .destructive) { model.resetRecords() }
        } message: {
            Text("This resets software update history and exclusions. A copy of the existing file is kept beside the new file. No app is installed or removed.")
        }
    }

    private var subtitle: String {
        guard let checked = model.lastChecked else { return "Choose app updates and review the installed version" }
        return "\(model.managedCaskCount) Homebrew packages checked · \(model.availableCount) selectable updates · \(checked.formatted(date: .abbreviated, time: .shortened))"
    }

    private var updatesPane: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                HStack {
                    Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                    TextField("Search app or package", text: $model.query).textFieldStyle(.roundedBorder)
                }
                .frame(maxWidth: 320)
                Toggle("Show excluded", isOn: $model.showExcluded).toggleStyle(.checkbox)
                Spacer()
                Button("Select all") { model.selectAllVisible() }.disabled(model.isBusy || model.visibleCandidates.isEmpty)
                Button("Select none") { model.selectNone() }.disabled(model.isBusy || model.selectedIDs.isEmpty)
            }
            .padding(.horizontal, 20).padding(.vertical, 10)
            if model.visibleCandidates.isEmpty {
                EmptyStateView(icon: model.isChecking ? "arrow.triangle.2.circlepath" : "checkmark.circle",
                               title: model.isChecking ? "Checking installed apps" : "No updates to show",
                               message: model.query.isEmpty
                                ? "Homebrew-managed packages appear here when an update is reported. Use Other apps for App Store and vendor-managed software."
                                : "Try another search or turn on Show excluded.")
            } else {
                Table(model.visibleCandidates) {
                    TableColumn("") { candidate in
                        Toggle("Select \(candidate.name)", isOn: Binding(
                            get: { model.selectedIDs.contains(candidate.id) },
                            set: { selected in
                                if selected { model.selectedIDs.insert(candidate.id) }
                                else { model.selectedIDs.remove(candidate.id) }
                            }))
                            .labelsHidden().toggleStyle(.checkbox).disabled(!model.isSelectable(candidate))
                    }
                    .width(26)
                    TableColumn("Application") { candidate in
                        VStack(alignment: .leading, spacing: 3) {
                            Text(candidate.name).fontWeight(.medium)
                            Text(candidate.caskID + " · Homebrew").font(.caption).foregroundStyle(.secondary)
                                .lineLimit(1).help(candidate.caskID)
                        }
                        .padding(.vertical, 5)
                    }
                    .width(min: 150, ideal: 230)
                    TableColumn("Installed") { candidate in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(candidate.installedVersion).font(.callout.monospacedDigit())
                            Text(candidate.appVersion == nil ? "Homebrew record" : "App version").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .width(min: 80, ideal: 100)
                    TableColumn("Available") { candidate in
                        Text(candidate.availableVersion).font(.callout.monospacedDigit())
                    }
                    .width(min: 80, ideal: 100)
                    TableColumn("Status") { candidate in
                        if model.activeCaskID == candidate.id {
                            Label("Updating…", systemImage: "arrow.down.circle").foregroundStyle(.orange)
                        } else if model.isExcluded(candidate.id) {
                            Chip(text: "Excluded", tint: .secondary)
                        } else if let reason = candidate.blockedReason {
                            Text(reason).font(.caption).foregroundStyle(.orange).lineLimit(3).help(reason)
                        } else {
                            Chip(text: "Ready", tint: .green)
                        }
                    }
                    .width(min: 100, ideal: 180)
                    TableColumn("") { candidate in
                        Menu {
                            Button("Version details…") { candidateDetails = candidate }
                            if model.isExcluded(candidate.id) {
                                Button("Include in updates") { model.restoreExclusions([candidate.id]) }
                                    .disabled(model.isBusy)
                            } else {
                                Button("Exclude from updates") { model.exclude([candidate]) }.disabled(model.isBusy)
                            }
                            if let homepage = candidate.homepage {
                                Button("Open developer website") { NSWorkspace.shared.open(homepage) }
                            }
                        } label: { Image(systemName: "ellipsis.circle") }
                        .menuStyle(.borderlessButton).fixedSize()
                    }
                    .width(28)
                }
                .tableStyle(.inset(alternatesRowBackgrounds: true))
            }
            Divider()
            HStack {
                Text("\(model.selectedCandidates.count) selected").font(.callout).foregroundStyle(.secondary)
                Button("Exclude selected") { model.exclude(model.selectedCandidates) }
                    .disabled(model.selectedCandidates.isEmpty || model.isBusy)
                Spacer()
                Button {
                    approved = model.selectedCandidates
                    confirmingUpdates = !approved.isEmpty
                } label: {
                    Label("Update selected (\(model.selectedCandidates.count))", systemImage: "arrow.down.circle")
                }
                .buttonStyle(.borderedProminent)
                .disabled(model.selectedCandidates.isEmpty || model.isBusy || otherOperationInProgress)
            }
            .padding(16)
        }
    }

    private var historyPane: some View {
        VStack(spacing: 0) {
            HStack {
                Text("Last 7 days").font(.headline)
                Picker("Status", selection: $historyFilter) {
                    Text("All results").tag(nil as SoftwareUpdateStatus?)
                    ForEach(SoftwareUpdateStatus.allCases, id: \.self) { value in Text(value.label).tag(Optional(value)) }
                }
                .frame(maxWidth: 250)
                Spacer()
                Button("Open records file") { NSWorkspace.shared.activateFileViewerSelecting([model.recordsURL]) }
                if model.canResetRecords {
                    Button("Reload records") { model.reloadRecords() }
                    Button("Reset records…", role: .destructive) { confirmingReset = true }
                }
                Button("Export…") { exportHistory() }.disabled(model.historyEntries.isEmpty)
                Button("Clear…", role: .destructive) { confirmingClear = true }.disabled(model.historyEntries.isEmpty || model.isBusy || model.canResetRecords)
            }
            .padding(.horizontal, 20).padding(.vertical, 12)
            let entries = model.historyEntries.filter { historyFilter == nil || $0.status == historyFilter }
            if entries.isEmpty {
                EmptyStateView(icon: "clock.arrow.circlepath", title: "No update attempts to show",
                               message: "Verified updates, failures and interrupted attempts stay here for seven days. Review a result for its installed-version evidence and provider output.")
            } else {
                List(entries) { entry in
                    HStack(spacing: 12) {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(entry.name).fontWeight(.medium)
                            Text("\(entry.installedVersionBefore) → \(entry.targetVersion) requested")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Chip(text: entry.status.label, tint: statusColor(entry.status))
                        Text(entry.date.formatted(date: .abbreviated, time: .shortened)).font(.caption).foregroundStyle(.secondary)
                            .frame(width: 140, alignment: .trailing)
                        Button("Details") { historyDetails = entry }
                    }
                    .padding(.vertical, 5)
                }
                .listStyle(.inset)
            }
        }
    }

    private var confirmationSheet: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Review selected app updates").font(.title2).bold()
            Text("Close the selected apps before continuing. Homebrew installs the listed available versions. If a version changes before an update starts, Evict asks you to review it again.")
                .font(.callout).foregroundStyle(.secondary)
            List(approved) { candidate in
                HStack {
                    VStack(alignment: .leading) {
                        Text(candidate.name).fontWeight(.medium)
                        Text(candidate.caskID).font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Text("\(candidate.installedVersion) → \(candidate.availableVersion)").font(.body.monospacedDigit())
                }
            }
            Text("Some vendor installers require Terminal or administrator interaction. Those attempts are reported for manual completion. This updater does not promise rollback for third-party apps.")
                .font(.caption).foregroundStyle(.secondary)
            HStack {
                Button("Cancel") { confirmingUpdates = false }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Update \(approved.count) app\(approved.count == 1 ? "" : "s")") {
                    let snapshot = approved
                    confirmingUpdates = false
                    Task {
                        guard !otherOperationInProgress else {
                            state.operationError = "Finish the current uninstall or Evict update before updating other applications."
                            return
                        }
                        await model.install(confirmed: snapshot)
                    }
                }
                .buttonStyle(.borderedProminent).disabled(model.isBusy || otherOperationInProgress)
            }
        }
        .padding(24).frame(width: 660, height: 430)
    }

    private var exclusionsSheet: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Update exclusions").font(.title2).bold()
            Text("Excluded packages stay out of Select all and cannot enter the update queue. Exclusions are saved for future checks.")
                .font(.callout).foregroundStyle(.secondary)
            if model.exclusions.isEmpty {
                EmptyStateView(icon: "line.3.horizontal.decrease.circle", title: "No excluded apps", message: "Use an app's menu or Exclude selected to save an exclusion.")
            } else {
                List(model.exclusions) { item in
                    HStack {
                        VStack(alignment: .leading, spacing: 4) {
                            Text(item.name).fontWeight(.medium)
                            Text(item.caskID).font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Include") { model.restoreExclusions([item.id]) }.disabled(model.isBusy)
                    }
                    .padding(.vertical, 4)
                }
            }
            if let error = model.errorMessage { Text(error).font(.caption).foregroundStyle(.orange) }
            HStack {
                Text("These preferences apply to Evict. Apps can still update themselves.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Done") { showingExclusions = false }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(24).frame(width: 640, height: 420)
    }

    private var otherAppsSheet: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("App Store and vendor updates").font(.title2).bold()
            Text("Use the App Store for Store apps. For other apps, open the app and choose its Check for Updates command. macOS updates are managed in System Settings.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                Button("Open App Store updates") { open("macappstore://showUpdatesPage") }
                Button("Open macOS Software Update") { open("x-apple.systempreferences:com.apple.Software-Update-Settings.extension") }
            }
            let vendorApps = state.apps.filter { $0.source != .system && $0.source != .homebrewCask && $0.source != .appStore }
            List(vendorApps) { app in
                HStack {
                    AppIcon(bundlePath: app.bundlePath)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(app.name).fontWeight(.medium)
                        Text(app.version ?? "Version unavailable").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Open app") { NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: app.bundlePath), configuration: .init(), completionHandler: nil) }
                }
            }
            HStack { Spacer(); Button("Done") { showingOtherApps = false }.keyboardShortcut(.cancelAction) }
        }
        .padding(24).frame(width: 660, height: 480)
    }

    private func candidateSheet(_ item: SoftwareUpdateCandidate) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(item.name).font(.title2).bold()
            Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 10) {
                valueRow("Provider", "Homebrew")
                valueRow("Package", item.caskID)
                valueRow("Installed app version", item.appVersion ?? "Not reported by this package")
                valueRow("Installed app build", item.appBuild ?? "Not reported")
                valueRow("Homebrew installation record", item.recordedVersion)
                valueRow("Available version", item.availableVersion)
                valueRow("Status", model.isExcluded(item.id) ? "Excluded" : item.blockedReason ?? "Ready for selection")
            }
            Text("The installed app version comes from app metadata when available. Homebrew's installation record can be older when an app has updated itself.")
                .font(.callout).foregroundStyle(.secondary)
            HStack {
                if let homepage = item.homepage { Link("Developer website", destination: homepage) }
                Spacer()
                Button("Done") { candidateDetails = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(24).frame(width: 650)
    }

    private func historySheet(_ entry: SoftwareUpdateHistoryEntry) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack { Text(entry.name).font(.title2).bold(); Spacer(); Chip(text: entry.status.label, tint: statusColor(entry.status)) }
            Grid(alignment: .leading, horizontalSpacing: 20, verticalSpacing: 8) {
                valueRow("Provider / package", "Homebrew · " + entry.caskID)
                valueRow("Started", entry.date.formatted(date: .abbreviated, time: .shortened))
                valueRow("Installed before", entry.installedVersionBefore)
                valueRow("Requested target", entry.targetVersion)
                valueRow("Installed after", entry.installedVersionAfter ?? "Not verified")
                valueRow("Homebrew record after", entry.recordedVersionAfter ?? "Unavailable")
                if let code = entry.exitStatus { valueRow("Provider exit code", String(code)) }
            }
            Text("Result and provider output").font(.headline)
            ScrollView {
                Text(entry.details).font(.system(.caption, design: .monospaced))
                    .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
            }
            .padding(12).background(.quaternary, in: RoundedRectangle(cornerRadius: 8))
            HStack {
                if entry.status != .running {
                    Button("Check again for this app") {
                        historyDetails = nil
                        pane = .updates
                        Task { await model.prepareRetry(entry) }
                    }
                    .disabled(model.isBusy)
                }
                Spacer()
                Button("Done") { historyDetails = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(24).frame(width: 710, height: 560)
    }

    private func valueRow(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label).foregroundStyle(.secondary)
            Text(value).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.callout)
    }

    private func notice(_ text: String, icon: String, color: Color) -> some View {
        Label(text, systemImage: icon).font(.callout).foregroundStyle(color)
            .frame(maxWidth: .infinity, alignment: .leading).textSelection(.enabled)
            .padding(.horizontal, 20).padding(.bottom, 10)
    }

    private func statusColor(_ status: SoftwareUpdateStatus) -> Color {
        switch status {
        case .updated, .alreadyCurrent: return .green
        case .failed: return .red
        case .running: return .blue
        case .unverified, .blocked, .interrupted: return .orange
        }
    }

    private func exportHistory() {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.nameFieldStringValue = "Evict-update-history.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        model.exportHistory(to: url)
    }

    private func open(_ url: String) { if let destination = URL(string: url) { NSWorkspace.shared.open(destination) } }
}
#endif
