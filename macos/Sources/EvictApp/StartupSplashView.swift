#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

struct StartupSplashView: View {
    @EnvironmentObject private var state: AppState
    var body: some View {
        VStack(spacing: 22) {
            Spacer()
            Image(nsImage: NSApplication.shared.applicationIconImage)
                .resizable().frame(width: 92, height: 92)
            Text("Evict").font(.system(size: 42, weight: .bold, design: .rounded))
            Text("A clearer view of your Mac").font(.title3).foregroundStyle(.secondary)
            VStack(spacing: 10) {
                ProgressView(value: state.startupProgress).progressViewStyle(.linear)
                Text(state.startupMessage).font(.callout).foregroundStyle(.secondary)
                HStack(spacing: 24) {
                    stage("Preferences", complete: state.startupProgress >= 0.15)
                    stage("Applications", complete: state.startupProgress >= 0.9)
                    stage("Ready", complete: state.startupComplete)
                }.font(.caption)
            }.frame(width: 420)
            if state.startupSlow {
                Text("The application scan is taking a little longer. You can continue while it finishes.")
                    .font(.callout).foregroundStyle(.secondary)
                HStack {
                    Button("Open Evict now") { state.openWhileScanning() }
                    Button("Open log") { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: AppPaths.logFile)]) }
                }
            }
            Spacer()
            Text("\(ReleaseManager.shared.runningVersion) · macOS").font(.caption).foregroundStyle(.secondary)
        }.padding(32).frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color(nsColor: .windowBackgroundColor))
    }
    private func stage(_ name: String, complete: Bool) -> some View {
        Label(name, systemImage: complete ? "checkmark.circle.fill" : "circle")
            .foregroundStyle(complete ? Color.accentColor : .secondary)
    }
}

struct SearchPalette: View {
    @EnvironmentObject private var state: AppState
    @Environment(\.dismiss) private var dismiss
    @State private var query = ""
    @FocusState private var focused: Bool
    private var pages: [AppState.Page] {
        AppState.Page.allCases.filter { query.isEmpty || $0.rawValue.localizedCaseInsensitiveContains(query) }
    }
    private var apps: [InstalledApp] {
        guard !query.trimmingCharacters(in: .whitespaces).isEmpty else { return [] }
        return Array(state.apps.filter { $0.name.localizedCaseInsensitiveContains(query) || ($0.bundleIdentifier ?? "").localizedCaseInsensitiveContains(query) }.prefix(20))
    }
    var body: some View {
        VStack(spacing: 0) {
            TextField("Search applications, updates, cleanup or settings", text: $query)
                .textFieldStyle(.roundedBorder).focused($focused).padding()
            List {
                Section("Go to") {
                    ForEach(pages) { page in
                        Button { state.page = page; dismiss() } label: { Label(page.rawValue, systemImage: page.icon) }
                            .buttonStyle(.plain)
                    }
                }
                if !apps.isEmpty {
                    Section("Applications") {
                        ForEach(apps) { app in
                            Button {
                                state.page = .applications
                                state.search = app.name
                                state.selection = [app.id]
                                dismiss()
                            } label: { HStack { AppIcon(bundlePath: app.bundlePath, size: 22); Text(app.name); Spacer(); Text(app.version ?? "").foregroundStyle(.secondary) } }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            HStack { Text("⌘K opens search").font(.caption).foregroundStyle(.secondary); Spacer(); Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }.padding()
        }.frame(width: 620, height: 450).onAppear { focused = true }
    }
}
#endif
