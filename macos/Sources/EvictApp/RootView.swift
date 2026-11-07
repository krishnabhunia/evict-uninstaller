#if os(macOS)
import SwiftUI
import EvictKit

struct RootView: View {
    @EnvironmentObject private var state: AppState
    @ObservedObject private var releases = ReleaseManager.shared
    @State private var showReleaseNotes = false

    var body: some View {
        NavigationSplitView {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 10) {
                    Image(systemName: "arrow.up.bin.fill").font(.title2).foregroundStyle(EvictTheme.accent)
                    Text("Evict").font(.system(.title, design: .rounded, weight: .bold)).foregroundStyle(.white)
                }
                .padding(.horizontal, 20).padding(.top, 24).padding(.bottom, 20)
                Button { state.showSearch = true } label: {
                    HStack {
                        Label("Search Evict", systemImage: "magnifyingglass")
                        Spacer()
                        Text("⌘K").font(.caption)
                    }
                    .foregroundStyle(.white.opacity(0.75)).padding(10)
                    .background(.white.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain).padding(.horizontal, 12)
                ScrollView {
                    VStack(alignment: .leading, spacing: 6) {
                        navigationGroup("WORKSPACE", pages: [.dashboard, .applications])
                        navigationGroup("MAINTENANCE", pages: [.softwareUpdater, .tools, .startupItems, .forceUninstall, .history])
                        navigationGroup("PREFERENCES", pages: [.versionManager, .settings])
                    }
                    .padding(.horizontal, 10).padding(.top, 20)
                }
                SidebarFooter()
            }
            .background(EvictTheme.sidebar)
            .navigationSplitViewColumnWidth(min: 205, ideal: 220, max: 270)
        } detail: {
            VStack(spacing: 0) {
                HStack(spacing: 10) {
                    Text("Evict").font(.headline)
                    Text("v\(releases.runningVersion)").font(.callout.monospacedDigit()).foregroundStyle(.secondary)
                    Spacer()
                    Button("What’s New", systemImage: "sparkles") { showReleaseNotes = true }.buttonStyle(.borderless)
                    if releases.hasAvailableUpdate, let version = releases.latestUpdateVersion {
                        Button("Update to v\(version)", systemImage: "arrow.down.circle.fill") { state.page = .versionManager }
                            .buttonStyle(.borderedProminent)
                    }
                }
                .padding(.horizontal, 20).padding(.vertical, 12)
                Divider()
                pageContent.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .tint(EvictTheme.accent)
        .sheet(item: Binding(get: { state.plan.map(PlanBox.init) }, set: { if $0 == nil { state.dismissPlan() } })) { box in
            UninstallSheet(plan: box.plan).environmentObject(state).interactiveDismissDisabled(state.isRemoving)
        }
        .sheet(isPresented: $showReleaseNotes) {
            ReleaseNotesView(version: releases.runningVersion, notes: releases.currentReleaseNotes)
        }
    }

    @ViewBuilder private var pageContent: some View {
        switch state.page {
        case .dashboard: DashboardView()
        case .applications: ApplicationsView()
        case .softwareUpdater: SoftwareUpdaterView()
        case .tools: ToolsView()
        case .forceUninstall: ForceUninstallView()
        case .startupItems: StartupItemsView()
        case .history: HistoryView()
        case .versionManager: VersionManagerView()
        case .settings: SettingsView()
        }
    }

    private func navigationGroup(_ title: String, pages: [AppState.Page]) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.system(size: 10, weight: .semibold)).tracking(1.2)
                .foregroundStyle(.white.opacity(0.42)).padding(.leading, 12).padding(.top, 14).padding(.bottom, 5)
            ForEach(pages) { page in
                Button { state.page = page } label: {
                    HStack(spacing: 10) {
                        Image(systemName: page.icon).frame(width: 19)
                        Text(page.rawValue)
                        Spacer(minLength: 0)
                    }
                    .font(.system(size: 13, weight: state.page == page ? .semibold : .regular))
                    .padding(.horizontal, 12).padding(.vertical, 10)
                    .foregroundStyle(state.page == page ? .white : .white.opacity(0.68))
                    .background(state.page == page ? EvictTheme.accent.opacity(0.85) : Color.clear,
                                in: RoundedRectangle(cornerRadius: 8))
                }
                .buttonStyle(.plain).accessibilityAddTraits(state.page == page ? .isSelected : [])
            }
        }
    }
}

struct PlanBox: Identifiable {
    let plan: UninstallPlan
    var id: String { plan.app.bundlePath + plan.app.name }
    init(_ plan: UninstallPlan) { self.plan = plan }
}

private struct SidebarFooter: View {
    @EnvironmentObject private var state: AppState
    var body: some View {
        VStack(alignment: .leading, spacing: 9) {
            Divider().overlay(.white.opacity(0.1))
            if !state.fullDiskAccess {
                Button { FullDiskAccess.openSettings() } label: {
                    Label("Grant Full Disk Access", systemImage: "lock.shield").font(.callout).foregroundStyle(.orange)
                }
                .buttonStyle(.plain).help(FullDiskAccess.explanation)
            }
            Text("\(state.apps.count) applications · \(ByteFormat.string(state.totalSize))")
                .font(.caption).foregroundStyle(.white.opacity(0.5))
        }.padding(16)
    }
}

#endif
