#if os(macOS)
import SwiftUI
import EvictKit

private struct StartupAction: Sendable {
    enum Kind: Sendable { case enable, disable, remove }
    let kind: Kind
    let item: StartupItem
    let inspection: StartupAgentInspection

    var title: String {
        switch kind {
        case .enable: return "Enable this startup item?"
        case .disable: return "Disable this startup item?"
        case .remove: return "Move this startup item to the Trash?"
        }
    }
    var button: String {
        switch kind {
        case .enable: return "Enable"
        case .disable: return "Disable"
        case .remove: return "Move to Trash"
        }
    }
    var explanation: String {
        let action: String
        switch kind {
        case .enable:
            action = "macOS will load this reviewed job. It may start its program immediately or when its configured trigger occurs."
        case .disable:
            action = "macOS will stop this job and prevent it from loading again. Its plist stays in place and can be enabled later."
        case .remove:
            action = "Evict will stop and verify the job before moving its plist to the Trash. Restore it from the Trash and enable it here if you need it again."
        }
        return "\(item.label)\n\(item.path)\n\n\(action)"
    }
}

@MainActor
private final class StartupItemsModel: ObservableObject {
    @Published private(set) var items: [StartupItem] = []
    @Published private(set) var inspections: [String: StartupAgentInspection] = [:]
    @Published private(set) var inspectionErrors: [String: String] = [:]
    @Published private(set) var isLoading = false
    @Published private(set) var isChanging = false
    @Published private(set) var message: String?
    @Published private(set) var hasError = false

    func reload() async {
        guard !isLoading, !isChanging else { return }
        isLoading = true
        let result = await Task.detached(priority: .userInitiated) {
            let items = LaunchItems().scan()
            return (items, StartupAgentControl().inspect(items))
        }.value
        items = result.0
        inspections = result.1.values
        inspectionErrors = result.1.errors
        isLoading = false
    }

    func perform(_ action: StartupAction, history: HistoryStore) async {
        guard !isChanging, !isLoading else { return }
        isChanging = true
        let outcome: Result<String, Error> = await Task.detached(priority: .userInitiated) {
            do {
                let controller = StartupAgentControl()
                switch action.kind {
                case .enable, .disable:
                    let result = try controller.setEnabled(action.kind == .enable, item: action.item, expected: action.inspection)
                    return .success("\(action.item.label): \(result.statusText).")
                case .remove:
                    let bytes = try controller.trash(action.item, expected: action.inspection)
                    history.append(HistoryEntry(appName: "Startup item: " + action.item.label, bundleIdentifier: nil,
                                               itemsRemoved: 1, bytesFreed: bytes, failures: 0))
                    let historyNote = history.lastError.map { " The history could not be saved: \($0)" } ?? ""
                    return .success("Moved \(action.item.label) to the Trash." + historyNote)
                }
            } catch { return .failure(error) }
        }.value
        switch outcome {
        case .success(let text): message = text; hasError = false
        case .failure(let error): message = error.localizedDescription; hasError = true
        }
        isChanging = false
        await reload()
    }
}

/// launchd agents and daemons – reversible controls are limited to the current user's owned jobs.
struct StartupItemsView: View {
    @EnvironmentObject private var state: AppState
    @StateObject private var model = StartupItemsModel()
    @ObservedObject private var updater = SoftwareUpdaterModel.shared
    @ObservedObject private var releases = ReleaseManager.shared
    @State private var pending: StartupAction?

    private var blocked: Bool {
        model.isLoading || model.isChanging || state.isRemoving || state.isPlanning ||
            state.isApplyingSchedule || updater.isUpdating || releases.isBusy
    }

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Startup Items",
                       subtitle: "\(model.items.count) jobs · \(model.items.filter(\.isOrphan).count) point at a missing program") {
                Button("Rescan", systemImage: "arrow.clockwise") { Task { await model.reload() } }
                    .disabled(model.isLoading || model.isChanging)
            }
            Text("Enable or disable your own launch agents without deleting their files. Apple, system and Evict scheduled-scan jobs are managed in their native settings.")
                .font(.callout).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 20).padding(.bottom, 10)
            if let message = model.message {
                Label(message, systemImage: model.hasError ? "exclamationmark.triangle" : "info.circle")
                    .font(.callout).foregroundStyle(model.hasError ? Color.orange : Color.secondary)
                    .textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 20).padding(.bottom, 8)
            }
            if model.isChanging { ProgressView("Verifying the startup change…").controlSize(.small).padding(8) }
            if model.isLoading && model.items.isEmpty {
                ProgressView().controlSize(.large).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if model.items.isEmpty {
                EmptyStateView(icon: "bolt.slash", title: "Nothing to show",
                               message: "No launch agents or daemons were found for your account.")
            } else {
                List {
                    ForEach(model.items) { item in
                        StartupRow(item: item, inspection: model.inspections[item.id],
                                   error: model.inspectionErrors[item.id], blocked: blocked,
                                   change: { kind in
                            guard !blocked, let inspection = model.inspections[item.id] else { return }
                            pending = StartupAction(kind: kind, item: item, inspection: inspection)
                        })
                    }
                }.listStyle(.inset)
            }
        }
        .task { await model.reload() }
        .alert(pending?.title ?? "Startup item", isPresented: Binding(
            get: { pending != nil }, set: { if !$0 { pending = nil } }), presenting: pending) { action in
            Button("Cancel", role: .cancel) { pending = nil }
            Button(action.button, role: action.kind == .enable ? nil : .destructive) {
                guard !blocked else { pending = nil; return }
                pending = nil
                Task { @MainActor in
                    guard !blocked else { return }
                    state.isRemoving = true
                    defer { state.isRemoving = false }
                    await model.perform(action, history: state.history)
                }
            }
        } message: { action in Text(action.explanation) }
    }
}

private struct StartupRow: View {
    let item: StartupItem
    let inspection: StartupAgentInspection?
    let error: String?
    let blocked: Bool
    let change: (StartupAction.Kind) -> Void

    private var needsEnable: Bool { inspection.map { $0.isDisabled || !$0.isLoaded } ?? false }

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: item.isDaemon ? "gearshape.2" : "person.crop.circle")
                .foregroundStyle(item.isOrphan ? .orange : .secondary)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.label).fontWeight(.medium)
                Text(item.program ?? item.path).font(.caption).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
                if let error { Text(error).font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer(minLength: 8)
            VStack(alignment: .trailing, spacing: 6) {
                HStack(spacing: 6) {
                    if item.isOrphan { Chip(text: "Program missing", tint: .orange) }
                    if let inspection {
                        Chip(text: inspection.statusText, tint: inspection.isDisabled ? .secondary : .green)
                    } else if item.isSystemScope { Chip(text: "System", tint: .secondary) }
                }
                HStack {
                    Button(needsEnable ? "Enable" : "Disable") { change(needsEnable ? .enable : .disable) }
                        .disabled(blocked || inspection == nil || (needsEnable && inspection?.canEnable != true))
                        .help(needsEnable ? "Load this reviewed user job" : "Stop this job while keeping its plist")
                    Button("Remove") { change(.remove) }
                        .disabled(blocked || inspection == nil)
                        .help("Verify that the job stopped, then move its plist to the Trash")
                }
            }
        }.padding(.vertical, 6).help(item.path)
    }
}
#endif
