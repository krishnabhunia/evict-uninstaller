#if os(macOS)
import SwiftUI
import AppKit
import EvictKit
import UniformTypeIdentifiers

/// Page title with an optional subtitle and trailing controls.
struct PageHeader<Trailing: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.title2).bold()
                Text(subtitle).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            trailing
        }
        .padding(.horizontal, 20)
        .padding(.top, 18)
        .padding(.bottom, 10)
    }
}

extension PageHeader where Trailing == EmptyView {
    init(title: String, subtitle: String) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}

/// Small rounded label used for the app source and the confidence tier.
struct Chip: View {
    let text: String
    var tint: Color = .secondary

    var body: some View {
        Text(text)
            .font(.caption2)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(tint.opacity(0.14), in: RoundedRectangle(cornerRadius: 4))
            .foregroundStyle(tint)
    }
}

extension Confidence {
    var tint: Color {
        switch self {
        case .high: return .green
        case .medium: return .orange
        case .low: return .gray
        }
    }
}

extension AppSource {
    var tint: Color {
        switch self {
        case .appStore: return .blue
        case .homebrewCask: return .brown
        case .installerPkg: return .purple
        case .dragInstalled: return .teal
        case .system: return .gray
        case .unknown: return .gray
        }
    }
}

/// Icon of an application bundle, read straight from disk.
struct AppIcon: View {
    let bundlePath: String
    var size: CGFloat = 28

    var body: some View {
        Image(nsImage: NSWorkspace.shared.icon(forFile: bundlePath))
            .resizable()
            .frame(width: size, height: size)
    }
}

struct EmptyStateView: View {
    let icon: String
    let title: String
    let message: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: icon).font(.system(size: 34)).foregroundStyle(.tertiary)
            Text(title).font(.headline)
            Text(message).font(.callout).foregroundStyle(.secondary).multilineTextAlignment(.center).frame(maxWidth: 380)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// Reads a dropped file URL. `loadDataRepresentation` works for every provider that offers
/// a file URL, including the Finder's, without relying on class-bridging behaviour.
enum DropReader {
    static func firstFileURL(_ providers: [NSItemProvider], completion: @escaping (URL) -> Void) -> Bool {
        guard let provider = providers.first(where: { $0.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) }) else { return false }
        provider.loadDataRepresentation(forTypeIdentifier: UTType.fileURL.identifier) { data, _ in
            guard let data, let url = URL(dataRepresentation: data, relativeTo: nil) else { return }
            completion(url)
        }
        return true
    }
}

#endif
