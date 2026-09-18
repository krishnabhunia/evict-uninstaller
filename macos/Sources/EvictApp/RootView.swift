#if os(macOS)
import SwiftUI
import EvictKit
import UniformTypeIdentifiers

struct RootView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        NavigationSplitView {
            List(selection: Binding<AppState.Page?>(get: { state.page }, set: { state.page = $0 ?? .applications })) {
                Section("Evict") {
                    ForEach(AppState.Page.allCases) { page in
                        Label(page.rawValue, systemImage: page.icon).tag(page)
                    }
                }
            }
            .listStyle(.sidebar)
            .navigationSplitViewColumnWidth(min: 190, ideal: 200, max: 240)
            .safeAreaInset(edge: .bottom) { SidebarFooter() }
        } detail: {
            Group {
                switch state.page {
                case .applications: ApplicationsView()
                case .forceUninstall: ForceUninstallView()
                case .startupItems: StartupItemsView()
                case .history: HistoryView()
                case .settings: SettingsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .sheet(item: Binding(get: { state.plan.map(PlanBox.init) }, set: { if $0 == nil { state.dismissPlan() } })) { box in
            UninstallSheet(plan: box.plan)
                .environmentObject(state)
        }
    }
}

/// `UninstallPlan` is not `Identifiable`; this wrapper lets it drive a sheet.
struct PlanBox: Identifiable {
    let plan: UninstallPlan
    var id: String { plan.app.bundlePath + plan.app.name }
    init(_ plan: UninstallPlan) { self.plan = plan }
}

private struct SidebarFooter: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if !state.fullDiskAccess {
                Button {
                    FullDiskAccess.openSettings()
                } label: {
                    Label("Grant Full Disk Access", systemImage: "lock.shield")
                        .font(.callout)
                }
                .buttonStyle(.link)
                .help(FullDiskAccess.explanation)
            }
            Text("\(state.apps.count) apps · \(ByteFormat.string(state.totalSize))")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 10)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
#endif
