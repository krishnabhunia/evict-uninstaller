#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

/// What Evict has removed, newest first.
struct HistoryView: View {
    @EnvironmentObject private var state: AppState
    @State private var entries: [HistoryEntry] = []
    @State private var confirmingClear = false

    private static let dateStyle: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .medium
        f.timeStyle = .short
        return f
    }()

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "History",
                       subtitle: entries.isEmpty ? "Nothing removed yet"
                                                 : "\(entries.count) uninstalls · \(ByteFormat.string(entries.reduce(0) { $0 + $1.bytesFreed })) freed in total") {
                Button("Clear", role: .destructive) { confirmingClear = true }
                    .disabled(entries.isEmpty)
            }

            if entries.isEmpty {
                EmptyStateView(icon: "clock", title: "No history yet",
                               message: "Every uninstall Evict carries out is recorded here, with what was removed and how much space it freed.")
            } else {
                List(entries) { entry in
                    HStack(spacing: 10) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text(entry.appName).fontWeight(.medium)
                            Text(entry.bundleIdentifier ?? "—").font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        if entry.failures > 0 { Chip(text: "\(entry.failures) failed", tint: .orange) }
                        Text("\(entry.itemsRemoved) items").font(.callout).foregroundStyle(.secondary)
                        Text(ByteFormat.string(entry.bytesFreed))
                            .font(.callout.monospacedDigit())
                            .frame(width: 74, alignment: .trailing)
                        Text(Self.dateStyle.string(from: entry.date))
                            .font(.caption).foregroundStyle(.secondary)
                            .frame(width: 150, alignment: .trailing)
                    }
                    .padding(.vertical, 3)
                }
                .listStyle(.inset)
            }
        }
        .onAppear { reload() }
        .onChange(of: state.removalResult == nil) { _ in reload() }
        .confirmationDialog("Clear the uninstall history?", isPresented: $confirmingClear) {
            Button("Clear history", role: .destructive) {
                state.history.clear()
                reload()
            }
        } message: {
            Text("This only clears Evict's own record. Nothing on disk changes.")
        }
    }

    private func reload() {
        state.history.load()
        entries = state.history.entries
    }
}
#endif
