#if os(macOS)
import SwiftUI
import AppKit
import EvictKit
import UniformTypeIdentifiers

/// The main page (design AT1–AT4): every installed application in a native Mac table. Click a row to
/// select it (⌘ / ⇧ for several), every second row is shaded, headers sort, columns can be moved,
/// resized, shown or hidden, and the layout is remembered.
struct ApplicationsView: View {
    @EnvironmentObject private var state: AppState
    @State private var isDropTargeted = false
    @State private var sortOrder: [KeyPathComparator<InstalledApp>] = [ApplicationsView.comparator(.name, ascending: true)]
    /// Column order, widths and hidden columns, kept in the user's defaults between launches.
    @AppStorage("applications.columns") private var columns = TableColumnCustomization<InstalledApp>()
    @AppStorage("applications.sortColumn") private var storedSortColumn = AppColumn.name.rawValue
    @AppStorage("applications.sortAscending") private var storedSortAscending = true
    @State private var restoredSort = false

    // ── sorting ──

    private static let keyPaths: [(AppColumn, PartialKeyPath<InstalledApp>)] = [
        (.name, \InstalledApp.name), (.version, \InstalledApp.versionText), (.bundleID, \InstalledApp.bundleIDText),
        (.source, \InstalledApp.sourceText), (.size, \InstalledApp.sizeBytes), (.installed, \InstalledApp.installedSortDate),
        (.lastUsed, \InstalledApp.lastUsedSortDate), (.location, \InstalledApp.bundlePath),
    ]

    static func comparator(_ column: AppColumn, ascending: Bool) -> KeyPathComparator<InstalledApp> {
        let order: SortOrder = ascending ? .forward : .reverse
        switch column {
        case .name: return KeyPathComparator(\InstalledApp.name, order: order)
        case .version: return KeyPathComparator(\InstalledApp.versionText, order: order)
        case .bundleID: return KeyPathComparator(\InstalledApp.bundleIDText, order: order)
        case .source: return KeyPathComparator(\InstalledApp.sourceText, order: order)
        case .size: return KeyPathComparator(\InstalledApp.sizeBytes, order: order)
        case .installed: return KeyPathComparator(\InstalledApp.installedSortDate, order: order)
        case .lastUsed: return KeyPathComparator(\InstalledApp.lastUsedSortDate, order: order)
        case .location: return KeyPathComparator(\InstalledApp.bundlePath, order: order)
        }
    }

    private var sortColumn: AppColumn {
        guard let keyPath = sortOrder.first?.keyPath else { return .name }
        return Self.keyPaths.first { $0.1 == keyPath }?.0 ?? .name
    }

    private var sortAscending: Bool { (sortOrder.first?.order ?? .forward) == .forward }

    private var rows: [InstalledApp] {
        ApplicationsTable.sorted(state.visibleApps, by: sortColumn, ascending: sortAscending)
    }

    private var selectedApps: [InstalledApp] { rows.filter { state.selection.contains($0.id) } }

    // ── page ──

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Applications",
                       subtitle: state.isScanning ? state.scanDetail
                                                  : "\(rows.count) of \(state.apps.count) shown · drag a column header to move it, right-click a header for more columns") {
                Button {
                    Task { await state.refreshApplications() }
                } label: {
                    Label("Rescan", systemImage: "arrow.clockwise")
                }
                .disabled(state.isScanning)
            }

            if state.isScanning && state.apps.isEmpty {
                ProgressView().controlSize(.large).frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                table
                Divider()
                selectionBar
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
        .onAppear(perform: restoreSort)
        .onChange(of: sortOrder) { _, _ in
            storedSortColumn = sortColumn.rawValue
            storedSortAscending = sortAscending
        }
        .onChange(of: state.apps) { _, apps in
            // Apps that were removed or are no longer listed can't stay selected.
            let ids = Set(apps.map(\.id))
            state.selection = state.selection.intersection(ids)
        }
    }

    private var table: some View {
        Table(rows, selection: $state.selection, sortOrder: $sortOrder, columnCustomization: $columns) {
            TableColumn("Name", value: \.name) { app in NameCell(app: app) }
                .width(min: 160, ideal: 240)
                .customizationID(AppColumn.name.rawValue)
                .disabledCustomizationBehavior(.visibility)

            TableColumn("Version", value: \.versionText) { app in
                Text(app.versionText).monospacedDigit().foregroundStyle(.secondary)
            }
            .width(min: 60, ideal: 110)
            .customizationID(AppColumn.version.rawValue)

            TableColumn("Bundle ID", value: \.bundleIDText) { app in
                Text(app.bundleIDText).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
            }
            .width(min: 100, ideal: 240)
            .customizationID(AppColumn.bundleID.rawValue)

            TableColumn("Source", value: \.sourceText) { app in
                Chip(text: app.source.rawValue, tint: app.source.tint)
            }
            .width(min: 90, ideal: 120)
            .customizationID(AppColumn.source.rawValue)

            TableColumn("Size", value: \.sizeBytes) { app in
                Text(ByteFormat.string(app.sizeBytes))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .width(min: 60, ideal: 84)
            .customizationID(AppColumn.size.rawValue)

            TableColumn("Installed", value: \.installedSortDate) { app in
                Text(Self.day(app.installedDate)).foregroundStyle(.secondary)
            }
            .width(min: 80, ideal: 110)
            .customizationID(AppColumn.installed.rawValue)
            .defaultVisibility(.hidden)

            TableColumn("Last used", value: \.lastUsedSortDate) { app in
                Text(Self.day(app.lastUsedDate)).foregroundStyle(.secondary)
            }
            .width(min: 80, ideal: 110)
            .customizationID(AppColumn.lastUsed.rawValue)
            .defaultVisibility(.hidden)

            TableColumn("Location", value: \.bundlePath) { app in
                Text((app.bundlePath as NSString).deletingLastPathComponent)
                    .foregroundStyle(.secondary).lineLimit(1).truncationMode(.head)
            }
            .width(min: 100, ideal: 180)
            .customizationID(AppColumn.location.rawValue)
            .defaultVisibility(.hidden)

            TableColumn("") { app in
                UninstallCell(app: app, busy: state.isPlanning || state.isRemoving) {
                    Task { await state.uninstall([app]) }
                }
            }
            .width(96)
            .customizationID("uninstall")
            .disabledCustomizationBehavior(.all)
        }
        .tableStyle(.inset)
        .alternatingRowBackgrounds(.enabled)
        .searchable(text: $state.search, placement: .toolbar, prompt: "Search applications")
        .contextMenu(forSelectionType: InstalledApp.ID.self) { ids in
            let picked = apps(for: ids)
            if !picked.isEmpty {
                Button(ApplicationsTable.uninstallLabel(count: picked.count)) { Task { await state.uninstall(picked) } }
                    .disabled(!picked.contains { $0.source.isRemovable } || state.isPlanning || state.isRemoving)
                Button("Show in Finder") { reveal(picked) }
                Button(picked.count > 1 ? "Copy bundle IDs" : "Copy bundle ID") { copyBundleIDs(picked) }
                    .disabled(!picked.contains { $0.bundleIdentifier != nil })
            }
        } primaryAction: { ids in
            // Double-click or Return: open the review for what was clicked.
            let picked = apps(for: ids)
            guard !picked.isEmpty else { return }
            Task { await state.uninstall(picked) }
        }
        .onExitCommand { state.selection = [] }
    }

    /// The bar under the table: what is selected, and what can be done with it.
    private var selectionBar: some View {
        let picked = selectedApps
        return HStack(spacing: 10) {
            Text(ApplicationsTable.selectionSummary(rows, selected: state.selection))
                .font(.callout)
                .foregroundStyle(picked.isEmpty ? Color.secondary : Color.primary)
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer()
            Button("Show in Finder") { reveal(picked) }
                .disabled(picked.isEmpty)
            Button(ApplicationsTable.uninstallLabel(count: picked.count)) {
                Task { await state.uninstall(picked) }
            }
            .keyboardShortcut(.delete, modifiers: .command)
            .buttonStyle(.borderedProminent)
            .disabled(!picked.contains { $0.source.isRemovable } || state.isPlanning || state.isRemoving)
            .help("Uninstall the selected apps one after another — each one is reviewed first (⌘⌫)")
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }

    // ── helpers ──

    private func apps(for ids: Set<InstalledApp.ID>) -> [InstalledApp] {
        rows.filter { ids.contains($0.id) }
    }

    private func reveal(_ apps: [InstalledApp]) {
        guard !apps.isEmpty else { return }
        NSWorkspace.shared.activateFileViewerSelecting(apps.map { URL(fileURLWithPath: $0.bundlePath) })
    }

    private func copyBundleIDs(_ apps: [InstalledApp]) {
        let ids = apps.compactMap(\.bundleIdentifier)
        guard !ids.isEmpty else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(ids.joined(separator: "\n"), forType: .string)
    }

    private func restoreSort() {
        guard !restoredSort else { return }
        restoredSort = true
        let column = AppColumn(rawValue: storedSortColumn) ?? .name
        sortOrder = [Self.comparator(column, ascending: storedSortAscending)]
    }

    private static func day(_ date: Date?) -> String {
        date.map { $0.formatted(date: .abbreviated, time: .omitted) } ?? "—"
    }

    private func handleDrop(_ providers: [NSItemProvider]) -> Bool {
        return DropReader.firstFileURL(providers) { url in
            guard AppInventory.validBundlePath(url.path) != nil else { return }
            Task { @MainActor in
                if let known = state.apps.first(where: { $0.bundlePath == url.path }) {
                    await state.uninstall([known])
                } else {
                    await state.prepareForcePlan(name: url.deletingPathExtension().lastPathComponent, bundlePath: url.path)
                }
            }
        }
    }
}

private struct NameCell: View {
    let app: InstalledApp

    var body: some View {
        HStack(spacing: 8) {
            AppIcon(bundlePath: app.bundlePath, size: 22)
            Text(app.name).fontWeight(.medium).lineLimit(1)
        }
        .help(app.bundlePath)
    }
}

/// Design choice B1: the one-click Uninstall button stays on every row.
private struct UninstallCell: View {
    let app: InstalledApp
    let busy: Bool
    let uninstall: () -> Void

    var body: some View {
        Button("Uninstall", action: uninstall)
            .controlSize(.small)
            .disabled(!app.source.isRemovable || busy)
            .help(app.source.isRemovable ? "Remove this app and its leftovers" : "Part of macOS – cannot be removed")
            .frame(maxWidth: .infinity, alignment: .trailing)
    }
}
#endif
