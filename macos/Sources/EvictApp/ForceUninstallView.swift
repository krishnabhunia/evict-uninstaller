#if os(macOS)
import SwiftUI
import AppKit
import EvictKit
import UniformTypeIdentifiers

/// For apps that are already dragged to the Trash, or that never appeared in the list:
/// search by name (and optionally a bundle) and clean up what is left behind.
struct ForceUninstallView: View {
    @EnvironmentObject private var state: AppState
    @State private var name = ""
    @State private var bundlePath: String?
    @State private var isTargeted = false

    var body: some View {
        VStack(spacing: 0) {
            PageHeader(title: "Force Uninstall",
                       subtitle: "Find what an app left behind, even when the app itself is gone")

            VStack(spacing: 18) {
                dropZone

                HStack(spacing: 8) {
                    TextField("Application name, e.g. Acme Writer", text: $name)
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(scan)
                    Button("Scan", action: scan)
                        .keyboardShortcut(.defaultAction)
                        .disabled(name.trimmingCharacters(in: .whitespaces).count < 3 || state.isPlanning)
                }

                if let bundlePath {
                    HStack {
                        Label(bundlePath, systemImage: "app.badge")
                            .font(.callout).lineLimit(1).truncationMode(.middle)
                        Spacer()
                        Button("Clear") { self.bundlePath = nil }
                    }
                }

                if state.isPlanning { ProgressView().controlSize(.small) }

                Label("Evict searches only your Library folders and the shared /Library. Anything it finds is listed for you to tick before it moves to the Trash.",
                      systemImage: "shield")
                    .font(.callout)
                    .foregroundStyle(.secondary)

                Spacer()
            }
            .padding(20)
        }
    }

    private var dropZone: some View {
        RoundedRectangle(cornerRadius: 12)
            .strokeBorder(isTargeted ? Color.accentColor : Color.secondary.opacity(0.4),
                          style: StrokeStyle(lineWidth: 2, dash: [7]))
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.secondary.opacity(isTargeted ? 0.1 : 0.04)))
            .frame(height: 130)
            .overlay {
                VStack(spacing: 6) {
                    Image(systemName: "arrow.down.app").font(.system(size: 26)).foregroundStyle(.secondary)
                    Text("Drop an application here").font(.headline)
                    Text("or type its name below").font(.callout).foregroundStyle(.secondary)
                }
            }
            .onDrop(of: [.fileURL], isTargeted: $isTargeted) { providers in
                DropReader.firstFileURL(providers) { url in
                    Task { @MainActor in
                        bundlePath = url.path
                        name = url.deletingPathExtension().lastPathComponent
                        scan()
                    }
                }
            }
    }

    private func scan() {
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard trimmed.count >= 3 else { return }
        Task { await state.prepareForcePlan(name: trimmed, bundlePath: bundlePath) }
    }
}
#endif
