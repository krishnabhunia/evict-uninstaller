#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

struct EvictApp: App {
    @StateObject private var state = AppState()
    @NSApplicationDelegateAdaptor(EvictApplicationDelegate.self) private var appDelegate

    var body: some Scene {
        Window("Evict", id: "evict-main") {
            Group {
                if state.startupComplete { RootView() }
                else { StartupSplashView() }
            }
                .environmentObject(state)
                .frame(minWidth: 900, minHeight: 560)
                .preferredColorScheme(state.colorScheme)
                .tint(Color(red: 0.93, green: 0.36, blue: 0.15))
                .task { appDelegate.state = state; await state.start() }
                .sheet(isPresented: $state.showSearch) { SearchPalette().environmentObject(state) }
                .alert("Evict", isPresented: Binding(get: { state.operationError != nil }, set: { if !$0 { state.operationError = nil } })) {
                    Button("OK", role: .cancel) { state.operationError = nil }
                } message: { Text(state.operationError ?? "") }
        }
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(replacing: .newItem) { }
            // Info window (IN1): F1, like the ⓘ beside the version and Settings → Info.
            CommandGroup(after: .appInfo) { InfoMenuCommand() }
            CommandGroup(after: .toolbar) {
                Button("Search Evict…") { state.showSearch = true }
                    .keyboardShortcut("k", modifiers: .command)
                Button("Refresh") { Task { await state.refreshApplications() } }
                    .keyboardShortcut("r", modifiers: .command)
                Button("Check for Evict Updates") {
                    state.page = .versionManager
                    Task { await ReleaseManager.shared.checkForUpdates() }
                }
            }
        }

        MenuBarExtra("Evict", systemImage: "trash.circle", isInserted: Binding(
            get: { state.settings.showMenuBarIcon }, set: { visible in state.updateSettings { $0.showMenuBarIcon = visible } })) {
            EvictMenuBar().environmentObject(state)
        }

        Settings {
            SettingsView().environmentObject(state).frame(minWidth: 660, minHeight: 600)
        }

        Window("Evict · Info", id: InfoModel.windowID) {
            InfoView()
                .preferredColorScheme(state.colorScheme)
        }
        .defaultSize(width: 820, height: 600)
    }
}

@MainActor
final class EvictApplicationDelegate: NSObject, NSApplicationDelegate {
    weak var state: AppState?
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard state?.isRemoving == true || state?.isPlanning == true || state?.isApplyingSchedule == true || SoftwareUpdaterModel.shared.isUpdating || ReleaseManager.shared.isBusy else {
            return .terminateNow
        }
        let alert = NSAlert()
        alert.messageText = "An operation is still running"
        alert.informativeText = "Finish the current cleanup, startup-item change, uninstall, scan-schedule change, software update or Evict version preparation before quitting. You can stop the update queue after its current application, or cancel version preparation."
        alert.addButton(withTitle: "Keep Evict open")
        alert.runModal()
        return .terminateCancel
    }
}

private struct EvictMenuBar: View {
    @EnvironmentObject private var state: AppState
    @Environment(\.openWindow) private var openWindow
    private func open(_ page: AppState.Page) {
        state.page = page
        openWindow(id: "evict-main")
        NSApp.activate(ignoringOtherApps: true)
    }
    var body: some View {
        Button("Open Evict") { open(.dashboard) }
        Button("Refresh Applications") { open(.applications); Task { await state.refreshApplications() } }
        Button("Check Software Updates") { open(.softwareUpdater); Task { await SoftwareUpdaterModel.shared.checkForUpdates() } }
        Button("Evict Versions") { open(.versionManager) }
        Divider()
        Button("Settings") { open(.settings) }
        Button("Quit Evict") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }
}
#endif
