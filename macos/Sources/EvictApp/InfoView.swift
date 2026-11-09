#if os(macOS)
import SwiftUI
import AppKit
import EvictKit

/// The Info window's tabs (design IN1).
enum InfoTab: Hashable {
    case build, features, whatsNew, hood
}

/// Everything the Info window shows, read once from the app bundle (written there by Scripts/make-app.sh).
final class InfoModel: ObservableObject {
    static let shared = InfoModel()
    static let windowID = "info"

    @Published var tab: InfoTab = .build
    let build: BuildInfo
    let features: [FeatureRow]
    let changelog: ChangelogSection?
    let changelogTitle: String

    init(bundle: Bundle = .main) {
        let details = BuildDetails.load(from: bundle.url(forResource: "build-info", withExtension: "json"))
        let version = bundle.object(forInfoDictionaryKey: "EvictReleaseVersion") as? String ?? Version.current
        build = BuildInfo(version: version, details: details, installedAs: BuildInfo.describeInstall(bundlePath: bundle.bundlePath))
        features = InfoContent.parseFeatures(Self.read(bundle, "FEATURES"))
        // This version's CHANGELOG section; the bundled release notes when the section is missing.
        if let section = InfoContent.changelogSection(Self.read(bundle, "CHANGELOG"), version: build.coreVersion) {
            changelog = section
            changelogTitle = "Evict for Mac \(section.heading)"
        } else {
            let notes = Self.read(bundle, "release-notes")?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            changelog = notes.isEmpty ? nil : ChangelogSection(heading: build.coreVersion, lines: notes.components(separatedBy: "\n"))
            changelogTitle = "Evict for Mac \(build.coreVersion)"
        }
    }

    private static func read(_ bundle: Bundle, _ name: String) -> String? {
        bundle.url(forResource: name, withExtension: "md").flatMap { try? String(contentsOf: $0, encoding: .utf8) }
    }

    var systemDescription: String {
        let os = ProcessInfo.processInfo.operatingSystemVersion
        #if arch(arm64)
        let architecture = "arm64"
        #else
        let architecture = "x86_64"
        #endif
        return "macOS \(os.majorVersion).\(os.minorVersion).\(os.patchVersion) \(architecture)"
    }

    var stackRows: [(item: String, value: String)] {
        [
            ("App", "Evict for Mac, written in Swift with SwiftUI (macOS 14 or later)"),
            ("Packaging", "Universal app (Apple silicon and Intel) in a disk image"),
            ("Storage", "Settings, history and the log file in ~/Library/Application Support/Evict"),
            ("Removing", "Everything goes to the Trash; nothing is deleted permanently"),
            ("Updates", "GitHub Releases (mac-v tags), chosen and installed from Evict Versions; test versions (betas) optional"),
            ("Design", "Follows the Light, Dark or System appearance set in Settings; system fonts only"),
        ]
    }
}

/// Opens the Info window on a tab; usable from views and menu commands.
struct InfoButton: View {
    @Environment(\.openWindow) private var openWindow
    var label: String? = nil

    var body: some View {
        Button {
            InfoModel.shared.tab = .build
            openWindow(id: InfoModel.windowID)
        } label: {
            if let label { Text(label) } else { Image(systemName: "info.circle") }
        }
        .help("Info (F1)")
        .accessibilityLabel("Info")
    }
}

/// Help-menu style command with the F1 shortcut.
struct InfoMenuCommand: View {
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        Button("Evict Info") {
            InfoModel.shared.tab = .build
            openWindow(id: InfoModel.windowID)
        }
        .keyboardShortcut(KeyEquivalent(Character(UnicodeScalar(NSF1FunctionKey)!)), modifiers: [])
    }
}

/// Header row and body rows of the plain-English tables, laid out with fixed first columns.
private struct TableHeader: View {
    let titles: [(String, CGFloat?)]
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            ForEach(Array(titles.enumerated()), id: \.offset) { _, title in
                if let width = title.1 {
                    Text(title.0).bold().frame(width: width, alignment: .leading)
                } else {
                    Text(title.0).bold().frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .font(.callout)
        .foregroundStyle(.secondary)
        .padding(.vertical, 6)
    }
}

struct InfoView: View {
    @ObservedObject private var model = InfoModel.shared
    @State private var query = ""
    @State private var status: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 44, height: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Evict · Info").font(.title2).bold()
                    Text(model.build.subtitle).foregroundStyle(.secondary)
                }
            }

            TabView(selection: $model.tab) {
                thisBuild.tabItem { Text("This build") }.tag(InfoTab.build)
                implemented.tabItem { Text("What's implemented") }.tag(InfoTab.features)
                whatsNew.tabItem { Text("What's new") }.tag(InfoTab.whatsNew)
                underTheHood.tabItem { Text("Under the hood") }.tag(InfoTab.hood)
            }

            HStack {
                if let status { Text(status).font(.caption).foregroundStyle(.secondary) }
                Spacer()
                Button("Copy build info") { copyBuildInfo() }
                Button("Close") { NSApp.keyWindow?.close() }
                    .keyboardShortcut(.cancelAction)
            }
        }
        .padding(20)
        .frame(minWidth: 720, minHeight: 520)
    }

    // ── This build ──
    private var thisBuild: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                if let banner = model.build.betaBanner {
                    Label(banner, systemImage: "exclamationmark.triangle")
                        .padding(10)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(Color.orange.opacity(0.15), in: RoundedRectangle(cornerRadius: 8))
                }
                TableHeader(titles: [("SrNo.", 50), ("Item", 130), ("This copy", nil)])
                Divider()
                ForEach(Array(model.build.rows().enumerated()), id: \.offset) { index, row in
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Text("\(index + 1)").frame(width: 50, alignment: .leading).foregroundStyle(.secondary)
                        Text(row.item).frame(width: 130, alignment: .leading)
                        if row.item == "Made from", let url = model.build.pullRequestURL {
                            Link(row.value, destination: url).frame(maxWidth: .infinity, alignment: .leading)
                        } else {
                            Text(row.value).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading)
                        }
                    }
                    Divider()
                }
            }
            .padding(12)
        }
    }

    // ── What's implemented ──
    private var implemented: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                TextField("Search features", text: $query).textFieldStyle(.roundedBorder).frame(maxWidth: 280)
                Spacer()
                Chip(text: "All areas", tint: .accentColor)
            }
            TableHeader(titles: [("SrNo.", 50), ("Area", 170), ("What it does", nil), ("Since", 60)])
            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(InfoContent.filter(model.features, query: query)) { row in
                        HStack(alignment: .firstTextBaseline, spacing: 12) {
                            Text("\(row.srNo)").frame(width: 50, alignment: .leading).foregroundStyle(.secondary)
                            Text(row.area).bold().frame(width: 170, alignment: .leading)
                            Text(row.whatItDoes).fixedSize(horizontal: false, vertical: true)
                                .frame(maxWidth: .infinity, alignment: .leading)
                            Text(row.since).frame(width: 60, alignment: .leading)
                        }
                        .padding(.vertical, 6)
                        Divider()
                    }
                }
            }
            Text(model.features.isEmpty ? "The feature list is not part of this build."
                 : "\(model.features.filter(\.onMac).count) features in this copy.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .padding(12)
    }

    // ── What's new ──
    private var whatsNew: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(model.changelogTitle).font(.headline)
                if model.build.isBeta {
                    Chip(text: model.build.betaNumber.map { "beta \($0) · being tested" } ?? "test build · being tested",
                         tint: .orange)
                }
            }
            ScrollView {
                Text(model.changelog?.text ?? "This build has no changelog section for its version yet.")
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            Link("All releases on GitHub", destination: URL(string: "https://github.com/krishnabhunia/evict-uninstaller/releases")!)
        }
        .padding(12)
    }

    // ── Under the hood ──
    private var underTheHood: some View {
        VStack(alignment: .leading, spacing: 8) {
            TableHeader(titles: [("SrNo.", 50), ("Part", 130), ("This copy uses", nil)])
            Divider()
            ForEach(Array(model.stackRows.enumerated()), id: \.offset) { index, row in
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    Text("\(index + 1)").frame(width: 50, alignment: .leading).foregroundStyle(.secondary)
                    Text(row.item).frame(width: 130, alignment: .leading)
                    Text(row.value).fixedSize(horizontal: false, vertical: true).frame(maxWidth: .infinity, alignment: .leading)
                }
                Divider()
            }
            HStack {
                Button("Open data folder") {
                    NSWorkspace.shared.open(URL(fileURLWithPath: AppPaths.supportDirectory))
                }
                Button("Open log file") {
                    NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: AppPaths.logFile)])
                }
            }
            .padding(.top, 6)
            Spacer()
        }
        .padding(12)
    }

    private func copyBuildInfo() {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        status = pasteboard.setString(model.build.copyText(system: model.systemDescription), forType: .string)
            ? "Build info copied." : "Could not copy – try again."
    }
}
#endif
