#if os(macOS)
import SwiftUI
import EvictKit

struct VersionManagerView: View {
    @EnvironmentObject private var state: AppState
    @ObservedObject private var manager = ReleaseManager.shared
    @ObservedObject private var softwareUpdater = SoftwareUpdaterModel.shared
    @State private var notes: NotesPresentation?
    @State private var confirmHandoff = false
    @State private var recoveryChoice: MacReleaseRecoveryCopy?
    @State private var installChoice: MacRelease?

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Version Manager", subtitle: "Evict \(manager.runningVersion) · macOS · Apple Silicon + Intel") {
                Button { Task { await manager.checkForUpdates() } } label: {
                    Label("Check now", systemImage: "arrow.clockwise")
                }
                .disabled(manager.isBusy || manager.isChecking)
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    statusPanel
                    preferencesPanel
                    releasesPanel
                    if let prepared = manager.prepared { preparedPanel(prepared) }
                    recoveryPanel
                    historyPanel
                }
                .padding(20)
            }
        }
        .task {
            await manager.reconcilePendingInstallation()
            if manager.releases.isEmpty { await manager.checkForUpdates() }
        }
        .sheet(item: $notes) { presentation in
            ReleaseNotesView(version: presentation.version, notes: presentation.body)
        }
        .alert("Prepare the selected version?", isPresented: Binding(
            get: { installChoice != nil }, set: { if !$0 { installChoice = nil } }), presenting: installChoice) { release in
            Button("Cancel", role: .cancel) { installChoice = nil }
            Button("Prepare") { if !otherOperationInProgress { manager.prepareVersion(release) }; installChoice = nil }
                .disabled(otherOperationInProgress)
        } message: { release in
            Text("Evict will download v\(release.version.rawValue), verify its checksum and application, and save a recovery copy of v\(manager.runningVersion). A separate Finder confirmation is required to replace the installed app.")
        }
        .alert("Open Finder and quit Evict?", isPresented: $confirmHandoff) {
            Button("Cancel", role: .cancel) { }
            Button("Open Finder & quit") {
                Task { await manager.openPreparedInFinderAndQuit { !otherOperationInProgress } }
            }
            .disabled(otherOperationInProgress)
        } message: {
            Text("Drag the verified Evict.app into the installation folder shown in Finder and choose Replace. Then open Evict again. Your recovery copy is saved. macOS may request permission or first-open approval.")
        }
        .alert("Prepare a recovery copy?", isPresented: Binding(
            get: { recoveryChoice != nil }, set: { if !$0 { recoveryChoice = nil } }), presenting: recoveryChoice) { copy in
                Button("Cancel", role: .cancel) { recoveryChoice = nil }
                Button("Prepare recovery") { if !otherOperationInProgress { manager.prepareRecovery(copy) }; recoveryChoice = nil }
                    .disabled(otherOperationInProgress)
            } message: { copy in
                Text("Verify the saved v\(copy.version) application and prepare it for restoration. Evict will preserve the current v\(manager.runningVersion) application before the Finder handoff.")
            }
    }

    private var statusPanel: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top) {
                Image(systemName: "shippingbox.fill").font(.system(size: 26)).foregroundStyle(.orange)
                VStack(alignment: .leading, spacing: 4) {
                    Text("Installed v\(manager.runningVersion)").font(.headline)
                    Text(manager.status).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Button("What's New") { notes = NotesPresentation(version: manager.runningVersion, body: manager.currentReleaseNotes) }
            }
            if manager.isChecking || manager.isBusy {
                HStack(spacing: 10) {
                    ProgressView().controlSize(.small)
                    Text(manager.isChecking ? "Checking releases…" : "Preparing the application…").font(.callout)
                    Spacer()
                    if manager.isBusy { Button("Cancel") { manager.cancelPreparation() } }
                }
            }
            if let error = manager.errorMessage {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange).font(.callout).textSelection(.enabled)
            }
            if let checked = manager.lastChecked {
                Text("Last checked \(checked.formatted(date: .abbreviated, time: .shortened))")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .padding(16).frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.primary.opacity(0.04), in: RoundedRectangle(cornerRadius: 12))
    }

    private var preferencesPanel: some View {
        GroupBox("Release preferences") {
            VStack(alignment: .leading, spacing: 12) {
                Toggle("Include beta releases", isOn: Binding(get: { manager.preferences.includeBeta }, set: manager.setIncludeBeta))
                Toggle("Show all published builds for version selection and rollback", isOn: Binding(get: { manager.preferences.showAllBuilds }, set: manager.setShowAllBuilds))
                Text("The default list shows the latest compatible release in each enabled channel. Historical releases are loaded from the most recent 1,000 GitHub releases.")
                    .font(.caption).foregroundStyle(.secondary)
                Divider()
                HStack {
                    if let pinned = manager.preferences.pinnedVersion {
                        Label("Pinned to v\(pinned)", systemImage: "pin.fill").foregroundStyle(.orange)
                        Spacer()
                        Button("Unpin") { manager.unpin() }
                    } else {
                        Text("Show an update button when a newer compatible version is found.").font(.callout)
                        Spacer()
                        Button("Pin installed version") { manager.pinInstalledVersion() }
                    }
                }
                if !manager.preferences.skippedTags.isEmpty {
                    HStack {
                        Text("\(manager.preferences.skippedTags.count) version(s) skipped for update notifications.")
                            .font(.caption).foregroundStyle(.secondary)
                        Spacer()
                        Button("Clear skipped versions") { manager.clearSkippedVersions() }
                    }
                }
            }
            .padding(8)
            .disabled(manager.isBusy)
        }
    }

    private var releasesPanel: some View {
        GroupBox("Choose the exact version") {
            VStack(alignment: .leading, spacing: 10) {
                if manager.visibleReleases.isEmpty {
                    Label("No compatible releases in this view.", systemImage: "shippingbox")
                        .foregroundStyle(.secondary).padding(.vertical, 14)
                } else {
                    ForEach(manager.visibleReleases) { release in
                        releaseRow(release)
                        if release.id != manager.visibleReleases.last?.id { Divider() }
                    }
                }
                if let selected = manager.selectedRelease {
                    Divider()
                    HStack {
                        Button("View implementation details") { notes = NotesPresentation(version: selected.version.rawValue, body: selected.notes) }
                        Link("Release page", destination: selected.pageURL)
                        Spacer()
                        Button("Skip notification") { manager.skipSelectedVersion() }
                            .disabled(manager.preferences.skippedTags.contains(selected.tag) || selected.version.rawValue == manager.runningVersion)
                        Button(preparationTitle(selected)) { installChoice = selected }
                            .buttonStyle(.borderedProminent).tint(.orange).disabled(otherOperationInProgress)
                    }
                    Text("Downloads are verified before use. Replacement is completed through Finder, followed by a verified relaunch.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(8).disabled(manager.isBusy || manager.isChecking)
        }
    }

    private func releaseRow(_ release: MacRelease) -> some View {
        Button { manager.selectedTag = release.tag } label: {
            HStack(alignment: .top, spacing: 12) {
                Image(systemName: manager.selectedTag == release.tag ? "largecircle.fill.circle" : "circle")
                    .foregroundStyle(manager.selectedTag == release.tag ? .orange : .secondary)
                    .font(.title3)
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text("v\(release.version)").font(.headline)
                        Chip(text: release.isBeta ? "Beta" : "Stable", tint: release.isBeta ? .orange : .green)
                        if release.tag == (release.isBeta ? manager.latestBetaTag : manager.latestStableTag) {
                            Chip(text: "Latest", tint: .blue)
                        }
                        if release.version.rawValue == manager.runningVersion { Chip(text: "Installed") }
                        if manager.preferences.skippedTags.contains(release.tag) { Chip(text: "Skipped") }
                        Spacer()
                    }
                    Text(release.notes.isEmpty ? "No implementation notes were provided for this release." : release.notes)
                        .font(.callout).foregroundStyle(.secondary).lineLimit(3).multilineTextAlignment(.leading)
                    HStack {
                        Text("macOS universal · \(ByteFormat.string(release.archive.size))")
                        if let published = release.publishedAt { Text(published.formatted(date: .abbreviated, time: .shortened)) }
                    }
                    .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(8)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }

    private func preparedPanel(_ ready: PreparedMacRelease) -> some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 10) {
                Label("Ready for v\(ready.entry.toVersion)", systemImage: "checkmark.shield.fill")
                    .font(.headline).foregroundStyle(.green)
                Text("A verified application and a recovery copy are ready. Open Finder, replace the installed application, and launch it again to verify completion.")
                    .font(.callout)
                Text("Destination: \(ready.entry.installationPath)").font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
                HStack {
                    Button("Cancel pending installation") { manager.cancelPendingInstallation() }
                    Spacer()
                    Button("Open Finder & quit Evict") { confirmHandoff = true }
                        .buttonStyle(.borderedProminent).tint(.orange)
                }
                .disabled(manager.isBusy || otherOperationInProgress)
            }
            .padding(8)
        }
    }

    private var recoveryPanel: some View {
        GroupBox("Recovery copies") {
            VStack(alignment: .leading, spacing: 10) {
                Text("A local copy of the running application is kept before each prepared update or rollback. Its checksum is checked again before restoration.")
                    .font(.caption).foregroundStyle(.secondary)
                if manager.recoveryCopies.isEmpty {
                    Text("No recovery copies yet.").foregroundStyle(.secondary).padding(.vertical, 8)
                }
                ForEach(manager.recoveryCopies) { copy in
                    HStack {
                        Image(systemName: "externaldrive.badge.timemachine").foregroundStyle(.orange)
                        VStack(alignment: .leading, spacing: 2) {
                            Text("Evict v\(copy.version)").font(.headline)
                            Text("\(copy.createdAt.formatted(date: .abbreviated, time: .shortened)) · \(ByteFormat.string(copy.archiveBytes))")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Prepare restore") { recoveryChoice = copy }
                            .disabled(manager.isBusy || manager.isChecking || otherOperationInProgress)
                    }
                }
            }
            .padding(8).frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private var historyPanel: some View {
        GroupBox("Version history") {
            VStack(alignment: .leading, spacing: 12) {
                if manager.history.isEmpty {
                    Text("No installation or recovery attempts yet.").foregroundStyle(.secondary).padding(.vertical, 8)
                }
                ForEach(manager.history) { entry in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text("\(entry.operation == .rollback ? "Recovery" : "Version change"): v\(entry.fromVersion) → v\(entry.toVersion)").font(.headline)
                            Spacer()
                            Chip(text: entry.status.title, tint: entry.status == .verified ? .green : .secondary)
                        }
                        Text(entry.detail).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                        Text(entry.createdAt.formatted(date: .abbreviated, time: .shortened)).font(.caption).foregroundStyle(.secondary)
                    }
                    if entry.id != manager.history.last?.id { Divider() }
                }
            }
            .padding(8).frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func preparationTitle(_ release: MacRelease) -> String {
        guard let running = MacReleaseVersion(manager.runningVersion) else { return "Prepare v\(release.version)" }
        if release.version == running { return "Prepare reinstall" }
        return release.version > running ? "Prepare upgrade" : "Prepare rollback"
    }

    private var otherOperationInProgress: Bool { state.isRemoving || state.isPlanning || state.isApplyingSchedule || softwareUpdater.isUpdating }
}

private struct NotesPresentation: Identifiable {
    let id = UUID()
    let version: String
    let body: String
}

struct ReleaseNotesView: View {
    let version: String
    let notes: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                VStack(alignment: .leading, spacing: 4) {
                    Text("What's New").font(.title2.bold())
                    Text("Evict v\(version) · macOS").foregroundStyle(.secondary)
                }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(20)
            Divider()
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    if notes.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                        Text("No implementation details were provided for this release.").foregroundStyle(.secondary)
                    } else {
                        ForEach(Array(notes.components(separatedBy: .newlines).enumerated()), id: \.offset) { _, line in
                            if line.hasPrefix("#") {
                                Text(line.drop(while: { $0 == "#" || $0 == " " })).font(.headline).padding(.top, 6)
                            } else if !line.isEmpty {
                                Text(line).font(.body).textSelection(.enabled)
                            }
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading).padding(20)
            }
        }
        .frame(width: 760, height: 540)
    }
}
#endif
