#if os(macOS)
import SwiftUI
import EvictKit

struct DashboardView: View {
    @EnvironmentObject private var state: AppState
    @ObservedObject private var maintenance = MacMaintenanceModel.shared
    @ObservedObject private var updates = SoftwareUpdaterModel.shared
    @ObservedObject private var releases = ReleaseManager.shared

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                hero
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 155))], spacing: 12) {
                    MetricCard(title: "Applications", value: state.isScanning && state.apps.isEmpty ? "—" : "\(state.apps.count)", icon: "square.grid.2x2")
                    MetricCard(title: "Application storage", value: ByteFormat.string(state.totalSize), icon: "internaldrive")
                    MetricCard(title: "Cleanup to review", value: maintenance.cleanup.map { ByteFormat.string($0.totalBytes) } ?? "—", icon: "sparkles")
                    MetricCard(title: "App updates", value: updates.lastChecked == nil ? "—" : "\(updates.availableCount)", icon: "arrow.down.circle", tint: .blue)
                }
                HStack {
                    Text("Worth a look").font(.title3).bold()
                    Spacer()
                    Text("Nothing is removed until you review it").font(.callout).foregroundStyle(.secondary)
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 245))], spacing: 14) {
                    MaintenanceActionCard(title: "Installed applications", detail: "Search, sort and review applications before uninstalling. Select several to work through a queue.",
                                          icon: "square.grid.2x2", button: "Browse applications") { state.page = .applications }
                    MaintenanceActionCard(title: "Software updates", detail: "Choose Homebrew application updates, manage exclusions and review the last seven days.",
                                          icon: "arrow.down.circle", button: "Check app updates") {
                        state.page = .softwareUpdater
                    }
                    MaintenanceActionCard(title: "Cache and log cleanup", detail: maintenance.cleanup.map { "\($0.candidates.count) items found. Review each candidate before moving it to the Trash." } ?? "Scan user caches and logs without touching shared roots or personal documents.",
                                          icon: "sparkles", button: "Review cleanup") { openTool(.cleanup) }
                    MaintenanceActionCard(title: "Startup items", detail: maintenance.cleanup == nil ? "Inspect launch agents and jobs that run at login." : "\(maintenance.startupItems.count) jobs found; \(maintenance.orphanCount) point to missing programs.",
                                          icon: "bolt.badge.clock", button: "Manage startup") { state.page = .startupItems }
                    MaintenanceActionCard(title: "Browser extensions", detail: "Inspect local extensions and broad permissions, then manage each in its own browser.",
                                          icon: "puzzlepiece.extension", button: "Inspect extensions") { openTool(.extensions) }
                    MaintenanceActionCard(title: "Install Monitor", detail: maintenance.recording == nil ? "Capture before and after snapshots to see what an installation changed." : "A recording is in progress. Finish it after installing or updating your application.",
                                          icon: "record.circle", button: maintenance.recording == nil ? "Open monitor" : "Continue recording") { openTool(.monitor) }
                }
                HStack {
                    Text("Your Mac’s settings").font(.title3).bold()
                    Spacer()
                    Button("Review security") { openTool(.security) }
                }
                VStack(spacing: 0) {
                    statusRow("Full Disk Access", detail: state.fullDiskAccess ? "Evict can inspect supported Library locations." : "Some leftovers may be hidden. Grant access in System Settings.",
                              enabled: state.fullDiskAccess, action: { FullDiskAccess.openSettings() })
                    Divider()
                    statusRow("Evict updates", detail: releases.hasAvailableUpdate ? "Version \(releases.latestUpdateVersion ?? "") is available to review." : "Choose an exact stable or beta release and read what it contains.",
                              enabled: !releases.hasAvailableUpdate, action: { state.page = .versionManager })
                }
                .padding(.horizontal, 16).background(EvictTheme.panel, in: RoundedRectangle(cornerRadius: 12))
                if let error = maintenance.error { Label(error, systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.orange) }
                if !maintenance.warnings.isEmpty {
                    Text("Some locations could not be inspected. Open Clean & tools to review scan notices.")
                        .font(.callout).foregroundStyle(.secondary)
                }
            }.padding(24).frame(maxWidth: 1300, alignment: .leading).frame(maxWidth: .infinity)
        }
        .task { if maintenance.cleanup == nil { await maintenance.scan() } }
    }

    private var hero: some View {
        HStack(alignment: .center, spacing: 25) {
            VStack(alignment: .leading, spacing: 9) {
                Text("YOUR MAC, IN VIEW").font(.system(size: 10, weight: .semibold)).tracking(2).foregroundStyle(EvictTheme.accent)
                Text("A clearer home for your applications.").font(.system(.title, design: .rounded, weight: .bold)).foregroundStyle(.white)
                Text(scanSummary).font(.callout).foregroundStyle(.white.opacity(0.7)).fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 10) {
                    Button {
                        Task {
                            await state.refreshApplications()
                            await maintenance.scan()
                        }
                    } label: {
                        Label(maintenance.isScanning || state.isScanning ? "Scanning…" : "Scan this Mac", systemImage: "arrow.clockwise")
                    }
                    .buttonStyle(.borderedProminent).tint(EvictTheme.accent)
                    .disabled(maintenance.isBusy || state.isScanning || state.isRemoving || updates.isUpdating)
                    if maintenance.isScanning || state.isScanning { ProgressView().controlSize(.small).tint(.white) }
                }.padding(.top, 5)
            }
            Spacer(minLength: 0)
            Image(systemName: "macwindow.on.rectangle").font(.system(size: 56, weight: .light))
                .foregroundStyle(.white.opacity(0.82)).accessibilityHidden(true)
        }
        .padding(26).background(EvictTheme.sidebar, in: RoundedRectangle(cornerRadius: 17))
    }

    private var scanSummary: String {
        if maintenance.isScanning { return "Inspecting caches, startup items, extensions and native security settings…" }
        if let scan = maintenance.cleanup {
            return "Last scan \(scan.date.formatted(date: .abbreviated, time: .shortened)). Review findings at your own pace."
        }
        return "Applications, updates and cleanup, together in one place."
    }

    private func openTool(_ tab: MacMaintenanceTab) {
        maintenance.tab = tab
        state.page = .tools
    }

    private func statusRow(_ title: String, detail: String, enabled: Bool, action: @escaping () -> Void) -> some View {
        HStack(spacing: 12) {
            Image(systemName: enabled ? "checkmark.circle" : "info.circle").foregroundStyle(enabled ? .green : EvictTheme.accent)
            VStack(alignment: .leading, spacing: 3) {
                Text(title).fontWeight(.medium)
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open", action: action)
        }.padding(.vertical, 14)
    }
}
#endif
