#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

struct SettingsView: View {
    @EnvironmentObject private var state: AppState
    @State private var pendingSchedule = ScanSchedule()
    @State private var showVersions = false
    @State private var report: ScheduledScanReport?

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Settings", subtitle: "Evict \(ReleaseManager.shared.runningVersion) · macOS") {
                InfoButton().buttonStyle(.borderless)
            }

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
                        .help("Items whose name only looks like the app's. They remain unchecked unless you enable pre-ticking below.")
                }

                Section("Scheduled scans") {
                    Picker("Frequency", selection: $pendingSchedule.frequency) {
                        ForEach(ScanSchedule.Frequency.allCases, id: \.self) { Text($0.rawValue).tag($0) }
                    }
                    if pendingSchedule.frequency != .never {
                        HStack {
                            Stepper("Hour: \(pendingSchedule.hour)", value: $pendingSchedule.hour, in: 0...23)
                            Stepper("Minute: \(pendingSchedule.minute)", value: $pendingSchedule.minute, in: 0...59)
                        }
                        if pendingSchedule.frequency == .weekly {
                            Picker("Day", selection: $pendingSchedule.weekday) {
                                ForEach(Array(["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].enumerated()), id: \.offset) { day in
                                    Text(day.element).tag(day.offset)
                                }
                            }
                        }
                    }
                    Text("Scans run at your Mac's local time while you are signed in, including when the window is closed. They record application and startup-item information. No files are removed and no updates are installed.")
                        .font(.callout).foregroundStyle(.secondary)
                    HStack {
                        Button("Apply schedule") { Task { await state.applySchedule(pendingSchedule) } }
                            .disabled(state.isApplyingSchedule || pendingSchedule == state.settings.scanSchedule)
                        Button("Cancel changes") { pendingSchedule = state.settings.scanSchedule }
                            .disabled(state.isApplyingSchedule || pendingSchedule == state.settings.scanSchedule)
                        if state.isApplyingSchedule { ProgressView().controlSize(.small) }
                    }
                    if let message = state.scheduleMessage { Text(message).font(.callout).textSelection(.enabled) }
                    if let report {
                        Text("Last scheduled scan: \(report.date.formatted()) · \(report.applicationCount) applications · \(report.orphanStartupItems) orphan startup items")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }

                Section("Updates and menu bar") {
                    Toggle("Check for Evict updates at startup", isOn: binding(\.checkForUpdates))
                    Toggle("Show Evict in the menu bar", isOn: binding(\.showMenuBarIcon))
                    Button("Versions, release notes and recovery") { showVersions = true }
                    Text("Choose an exact macOS stable or beta release. Downloads and recovery copies are verified before Finder opens them.")
                        .font(.callout).foregroundStyle(.secondary)
                }

                Section("Removing") {
                    Toggle("Ask before moving anything to the Trash", isOn: binding(\.confirmBeforeRemoving))
                    Toggle("Pre-tick low-confidence finds", isOn: binding(\.preselectLowConfidence))
                        .help("Off by default. Vendor-only or shared matches still require a manual choice; a wrong tick removes another app's data.")
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
                            Task { await state.resetSettings() }
                        }
                        .disabled(state.isApplyingSchedule)
                        Spacer()
                        InfoButton(label: "Info")
                        Button("Open Evict's log") {
                            NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: AppPaths.logFile)])
                        }
                    }
                }
            }
            .formStyle(.grouped)
        }
        .onAppear { pendingSchedule = state.settings.scanSchedule; report = ScheduledScanReport.load() }
        .onChange(of: state.settings.scanSchedule) { value in pendingSchedule = value }
        .sheet(isPresented: $showVersions) {
            VStack {
                VersionManagerView()
                Button("Close") { showVersions = false }.keyboardShortcut(.cancelAction).padding(.bottom)
            }.frame(minWidth: 900, minHeight: 620)
        }
    }

    private func binding(_ keyPath: WritableKeyPath<EvictKit.Settings, Bool>) -> Binding<Bool> {
        Binding(get: { state.settings[keyPath: keyPath] },
                set: { value in state.updateSettings { $0[keyPath: keyPath] = value } })
    }
}
#endif
