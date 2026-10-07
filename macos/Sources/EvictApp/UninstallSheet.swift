#if os(macOS)
import SwiftUI
import EvictKit

/// The three-step uninstall: what was found → review and tick → done.
struct UninstallSheet: View {
    let plan: UninstallPlan
    @EnvironmentObject private var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var step: Step = .review
    @State private var confirmingRemoval = false

    enum Step { case review, working, done }

    private var selectedItems: [LeftoverItem] { plan.allItems.filter { state.planSelection.contains($0.id) } }
    private var selectedBytes: Int64 { selectedItems.reduce(0) { $0 + $1.sizeBytes } }

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            footer
        }
        .frame(width: 720, height: 560)
        .interactiveDismissDisabled(step == .working || state.isRemoving)
        .confirmationDialog("Move \(selectedItems.count) selected items to the Trash?", isPresented: $confirmingRemoval) {
            Button("Move to Trash", role: .destructive) { startRemoval() }
            Button("Cancel", role: .cancel) { }
        } message: {
            Text("Review shared or uncertain matches before continuing. Items stay in the Trash until you empty it.")
        }
    }

    private var header: some View {
        HStack(spacing: 12) {
            AppIcon(bundlePath: plan.app.bundlePath, size: 40)
            VStack(alignment: .leading, spacing: 2) {
                Text(step == .done ? "Removal results for \(plan.app.name)" : "Uninstall \(plan.app.name)")
                    .font(.title3).bold()
                Text(subtitle).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
        }
        .padding(16)
    }

    private var subtitle: String {
        switch step {
        case .review:
            return "\(plan.allItems.count) items found · \(ByteFormat.string(plan.totalBytes)) · everything goes to the Trash"
        case .working:
            return "Moving items to the Trash…"
        case .done:
            guard let result = state.removalResult else { return "" }
            return "\(result.succeededCount) items moved to Trash · \(ByteFormat.string(result.bytesFreed)) moved"
        }
    }

    @ViewBuilder private var content: some View {
        switch step {
        case .review: reviewList
        case .working: ProgressView().controlSize(.large).frame(maxWidth: .infinity, maxHeight: .infinity)
        case .done: doneView
        }
    }

    private var reviewList: some View {
        List {
            if let cask = plan.homebrewCask {
                Section {
                    Label("Installed with Homebrew. Removing it here leaves Homebrew's record behind – run \"\(HomebrewService.uninstallCommand(cask: cask))\" in Terminal instead to keep Homebrew tidy.",
                          systemImage: "info.circle")
                        .font(.callout)
                }
            }
            ForEach(plan.groups) { group in
                Section {
                    ForEach(group.items) { item in
                        LeftoverRow(item: item, isOn: Binding(
                            get: { state.planSelection.contains(item.id) },
                            set: { on in
                                if on { state.planSelection.insert(item.id) } else { state.planSelection.remove(item.id) }
                            }))
                    }
                } header: {
                    HStack {
                        Text(group.kind.rawValue)
                        Spacer()
                        Text("\(group.items.count) · \(ByteFormat.string(group.totalBytes))")
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
        .listStyle(.inset)
    }

    private var doneView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                if let result = state.removalResult {
                    Label("\(result.succeededCount) items moved to the Trash (\(ByteFormat.string(result.bytesFreed))). Disk space is reclaimed when the Trash is emptied. You can restore the moved items from the Trash.",
                          systemImage: result.failed.isEmpty ? "checkmark.circle" : "exclamationmark.triangle")
                        .font(.callout)

                    if let bundle = plan.bundleItem, !result.trashed.contains(bundle.path) {
                        Label("The application bundle was not removed. Only successful selected items were moved.",
                              systemImage: "app.badge")
                            .font(.callout).foregroundStyle(.orange)
                    }

                    if result.needsAdminCount > 0 {
                        Label("\(result.needsAdminCount) item(s) require administrator authorization. For files, use Finder to authorize the move; for installer receipts, follow the command listed below.",
                              systemImage: "lock")
                            .font(.callout)
                            .foregroundStyle(.orange)
                    }
                    if !result.failed.isEmpty {
                        VStack(alignment: .leading, spacing: 6) {
                            Text("Not removed").font(.headline)
                            ForEach(result.failed.indices, id: \.self) { index in
                                VStack(alignment: .leading, spacing: 1) {
                                    Text(result.failed[index].path).font(.caption).textSelection(.enabled)
                                    Text(result.failed[index].error).font(.caption2).foregroundStyle(.secondary)
                                }
                            }
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(16)
        }
    }

    private var footer: some View {
        HStack {
            if step == .review {
                Button(state.planSelection.count == plan.allItems.count ? "Deselect all" : "Select all") {
                    state.planSelection = state.planSelection.count == plan.allItems.count ? [] : Set(plan.allItems.map(\.id))
                }
                Text("\(selectedItems.count) selected · \(ByteFormat.string(selectedBytes))")
                    .font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            Button(step == .done ? "Close" : "Cancel") { dismiss() }
                .keyboardShortcut(.cancelAction)
                .disabled(step == .working || state.isRemoving)
            if step == .review {
                Button("Move to Trash") {
                    if state.settings.confirmBeforeRemoving { confirmingRemoval = true }
                    else { startRemoval() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(selectedItems.isEmpty)
            }
        }
        .padding(16)
    }

    private func startRemoval() {
        guard !state.isRemoving, !selectedItems.isEmpty else { return }
        step = .working
        Task {
            await state.performRemoval()
            step = .done
        }
    }
}

private struct LeftoverRow: View {
    let item: LeftoverItem
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: 10) {
            Toggle("", isOn: $isOn).labelsHidden()
            VStack(alignment: .leading, spacing: 1) {
                Text(item.displayName).lineLimit(1).truncationMode(.middle)
                Text(item.reason).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if item.needsAdmin { Chip(text: "Admin", tint: .orange) }
            if item.isSharedVendorMatch == true { Chip(text: "Shared vendor", tint: .orange) }
            Chip(text: item.confidence.label, tint: item.confidence.tint)
            Text(ByteFormat.string(item.sizeBytes))
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 66, alignment: .trailing)
        }
        .help(item.path)
    }
}
#endif
