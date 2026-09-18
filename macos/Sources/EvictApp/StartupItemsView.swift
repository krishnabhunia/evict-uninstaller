#if os(macOS)
import SwiftUI
import EvictKit

/// launchd agents and daemons – the Mac equivalent of the Windows startup list.
struct StartupItemsView: View {
    @State private var items: [StartupItem] = []
    @State private var isLoading = false
    @State private var message: String?
    @State private var pendingRemoval: StartupItem?

    private let launchItems = LaunchItems()
    private let remover = Remover()

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Startup Items",
                       subtitle: items.isEmpty ? "Background jobs that start with your Mac"
                                               : "\(items.count) jobs · \(items.filter(\.isOrphan).count) point at a program that is gone") {
                Button {
                    reload()
                } label: {
                    Label("Rescan", systemImage: "arrow.clockwise")
                }
                .disabled(isLoading)
            }

            if let message {
                Label(message, systemImage: "info.circle")
                    .font(.callout)
                    .padding(.horizontal, 20).padding(.bottom, 8)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            if isLoading {
                ProgressView().controlSize(.large).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if items.isEmpty {
                EmptyStateView(icon: "bolt.slash", title: "Nothing to show",
                               message: "No launch agents or daemons were found for your account.")
            } else {
                List {
                    ForEach(items) { item in
                        StartupRow(item: item) { pendingRemoval = item }
                    }
                }
                .listStyle(.inset)
            }
        }
        .task { if items.isEmpty { reload() } }
        .alert("Move this startup item to the Trash?", isPresented: Binding(
            get: { pendingRemoval != nil }, set: { if !$0 { pendingRemoval = nil } })) {
            Button("Cancel", role: .cancel) { pendingRemoval = nil }
            Button("Move to Trash", role: .destructive) { remove() }
        } message: {
            Text(pendingRemoval.map { "\($0.label)\n\($0.path)\n\nThe job is unloaded first, then its file goes to the Trash. You can put it back at any time." } ?? "")
        }
    }

    private func reload() {
        isLoading = true
        Task.detached(priority: .userInitiated) {
            let found = LaunchItems().scan()
            await MainActor.run {
                items = found
                isLoading = false
            }
        }
    }

    private func remove() {
        guard let item = pendingRemoval else { return }
        pendingRemoval = nil
        launchItems.unload(item)
        do {
            _ = try remover.trash(item.path)
            message = "Moved \(item.label) to the Trash."
            reload()
        } catch {
            message = "Could not remove \(item.label): \(error.localizedDescription)"
        }
    }
}

private struct StartupRow: View {
    let item: StartupItem
    let remove: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: item.isDaemon ? "gearshape.2" : "person.crop.circle")
                .foregroundStyle(item.isOrphan ? .orange : .secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(item.label).fontWeight(.medium)
                Text(item.program ?? item.path)
                    .font(.caption).foregroundStyle(.secondary)
                    .lineLimit(1).truncationMode(.middle)
            }
            Spacer()
            if item.isOrphan { Chip(text: "Program missing", tint: .orange) }
            if item.runsAtLoad { Chip(text: "Runs at login", tint: .blue) }
            if item.isSystemScope { Chip(text: "Admin", tint: .gray) }
            Button("Remove", action: remove)
                .disabled(item.isSystemScope)
                .help(item.isSystemScope ? "Needs administrator rights" : "Unload the job and move its file to the Trash")
        }
        .padding(.vertical, 3)
        .help(item.path)
    }
}
#endif
