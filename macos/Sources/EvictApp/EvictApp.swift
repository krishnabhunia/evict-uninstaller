#if os(macOS)
import SwiftUI
import EvictKit

@main
struct EvictApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup {
            RootView()
                .environmentObject(state)
                .frame(minWidth: 900, minHeight: 560)
                .preferredColorScheme(state.colorScheme)
                .task { await state.refreshApplications() }
        }
        .windowToolbarStyle(.unified)
        .commands {
            CommandGroup(replacing: .newItem) { }
            CommandGroup(after: .toolbar) {
                Button("Refresh") { Task { await state.refreshApplications() } }
                    .keyboardShortcut("r", modifiers: .command)
            }
        }

        Settings {
            SettingsView().environmentObject(state)
        }
    }
}
#endif
