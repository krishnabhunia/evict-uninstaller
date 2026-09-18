#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

struct SettingsView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Settings", subtitle: "Evict \(Version.current)")

            Form {
                Section("Appearance") {
                    Picker("Theme", selection: Binding(
                        get: { state.settings.appearance },
                        set: { value in state.updateSettings { $0.appearance = value } })) {
                        ForEach(["System", "Light", "Dark"], id: \.self) { Text($0).tag($0) }
                    }
                    .pickerStyle(.segmented)
                }

                Section("Scanning") {
                    Toggle("Include apps that ship with macOS", isOn: binding(\.includeSystemApps))
                        .help("System apps are listed for reference only – they can never be removed.")
                    Toggle("Also search the shared /Library folder", isOn: binding(\.scanSystemLocations))
                    Toggle("Show low-confidence finds", isOn: binding(\.showLowConfidenceItems))
                        .help("Items whose name only looks like the app's. They are never ticked automatically.")
                }

                Section("Removing") {
                    Toggle("Ask before moving anything to the Trash", isOn: binding(\.confirmBeforeRemoving))
                    Toggle("Pre-tick low-confidence finds", isOn: binding(\.preselectLowConfidence))
                        .help("Off by default on purpose – a wrong tick removes another app's data.")
                    Label("Everything Evict removes goes to the Trash. Nothing is deleted permanently.",
                          systemImage: "trash.slash")
                        .font(.callout).foregroundStyle(.secondary)
                }

                Section("Permissions") {
                    HStack {
                        Label(state.fullDiskAccess ? "Full Disk Access is granted" : "Full Disk Access is not granted",
                              systemImage: state.fullDiskAccess ? "checkmark.shield" : "exclamationmark.shield")
                            .foregroundStyle(state.fullDiskAccess ? .green : .orange)
                        Spacer()
                        Button("Open System Settings") { FullDiskAccess.openSettings() }
                    }
                    Text(FullDiskAccess.explanation).font(.callout).foregroundStyle(.secondary)
                }

                Section {
                    HStack {
                        Button("Reset to defaults") {
                            state.settingsStore.reset()
                            state.updateSettings { _ in }
                        }
                        Spacer()
                        Button("Open Evict's log") {
                            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: AppPaths.logFile)])
                        }
                    }
                }
            }
            .formStyle(.grouped)
        }
    }

    private func binding(_ keyPath: WritableKeyPath<Settings, Bool>) -> Binding<Bool> {
        Binding(get: { state.settings[keyPath: keyPath] },
                set: { value in state.updateSettings { $0[keyPath: keyPath] = value } })
    }
}
#endif
