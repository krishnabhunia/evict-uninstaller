#if os(macOS)
import SwiftUI
import AppKit
import EvictKit
import UniformTypeIdentifiers

/// The main page: every installed application, with search, sorting and the uninstall action.
struct ApplicationsView: View {
    @EnvironmentObject private var state: AppState
    @State private var sort: SortOrder = .name
    @State private var isDropTargeted = false

    enum SortOrder: String, CaseIterable, Identifiable {
        case name = "Name", size = "Size", installed = "Installed", lastUsed = "Last used"
        var id: String { rawValue }
    }

    private var rows: [InstalledApp] {
        switch sort {
        case .name: return state.visibleApps
        case .size: return state.visibleApps.sorted { $0.sizeBytes > $1.sizeBytes }
        case .installed: return state.visibleApps.sorted { ($0.installedDate ?? .distantPast) > ($1.installedDate ?? .distantPast) }
        case .lastUsed: return state.visibleApps.sorted { ($0.lastUsedDate ?? .distantPast) > ($1.lastUsedDate ?? .distantPast) }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Applications",
                       subtitle: state.isScanning ? state.scanDetail : "\(rows.count) of \(state.apps.count) shown") {
                HStack(spacing: 10) {
                    Picker("", selection: $sort) {
                        ForEach(SortOrder.allCases) { Text($0.rawValue).tag($0) }
                    }
                    .labelsHidden()
                    .frame(width: 130)

                    Button {
                        Task { await state.refreshApplications() }
                    } label: {
                        Label("Rescan", systemImage: "arrow.clockwise")
                    }
                    .disabled(state.isScanning)
                }
            }

            if state.isScanning && state.apps.isEmpty {
                ProgressView().controlSize(.large).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List {
                    ForEach(rows) { app in
                        AppRow(app: app) {
                            Task { await state.preparePlan(for: app) }
                        }
                        .listRowSeparator(.visible)
                    }
                }
                .listStyle(.inset)
                .searchable(text: $state.search, placement: .toolbar, prompt: "Search applications")
            }
        }
        .overlay {
            if isDropTargeted {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 2, dash: [6]))
                    .background(Color.accentColor.opacity(0.06))
                    .overlay(Text("Drop an app here to uninstall it").font(.headline))
                    .padding(12)
            }
        }
        .onDrop(of: [.fileURL], isTargeted: $isDropTargeted) { providers in
            handleDrop(providers)
        }
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        return DropReader.firstFileURL(providers) { url in
            guard url.pathExtension == "app" else { return }
            Task { @MainActor in
                if let known = state.apps.first(where: { $0.bundlePath == url.path }) {
                    await state.preparePlan(for: known)
                } else {
                    await state.prepareForcePlan(name: url.deletingPathExtension().lastPathComponent, bundlePath: url.path)
                }
            }
        }
    }
}

private struct AppRow: View {
    let app: InstalledApp
    let uninstall: () -> Void
    @State private var hovering = false

    var body: some View {
        HStack(spacing: 12) {
            AppIcon(bundlePath: app.bundlePath, size: 30)

            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(app.name).fontWeight(.medium)
                    if let version = app.version { Text(version).font(.caption).foregroundStyle(.secondary) }
                }
                Text(app.bundleIdentifier ?? app.bundlePath)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            Spacer()

            Chip(text: app.source.rawValue, tint: app.source.tint)
            Text(ByteFormat.string(app.sizeBytes))
                .font(.callout.monospacedDigit())
                .foregroundStyle(.secondary)
                .frame(width: 74, alignment: .trailing)

            Button(action: uninstall) {
                Label("Uninstall", systemImage: "trash")
                    .labelStyle(.titleOnly)
            }
            .disabled(!app.source.isRemovable)
            .help(app.source.isRemovable ? "Remove this app and its leftovers" : "Part of macOS – cannot be removed")
            .opacity(hovering || !app.source.isRemovable ? 1 : 0.75)
        }
        .padding(.vertical, 4)
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .contextMenu {
            Button("Uninstall…", action: uninstall).disabled(!app.source.isRemovable)
            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: app.bundlePath)]) }
        }
    }
}
#endif
