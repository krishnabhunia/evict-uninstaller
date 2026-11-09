import Foundation

/// One row of docs/FEATURES.md (shown on the Info window's "What's implemented" tab).
public struct FeatureRow: Equatable, Identifiable, Sendable {
    public let srNo: Int
    public let area: String
    public let whatItDoes: String
    public let since: String
    public let onWindows: Bool
    public let onMac: Bool
    public var id: Int { srNo }

    public init(srNo: Int, area: String, whatItDoes: String, since: String, onWindows: Bool, onMac: Bool) {
        self.srNo = srNo
        self.area = area
        self.whatItDoes = whatItDoes
        self.since = since
        self.onWindows = onWindows
        self.onMac = onMac
    }
}

/// A CHANGELOG section shown on the "What's new" tab.
public struct ChangelogSection: Equatable, Sendable {
    public let heading: String
    public let lines: [String]
    public var text: String { lines.joined(separator: "\n") }

    public init(heading: String, lines: [String]) {
        self.heading = heading
        self.lines = lines
    }
}

/// Build details written by `Scripts/make-app.sh` into `Contents/Resources/build-info.json`
/// (design IN1). CI fills them; a local build has only the build time.
public struct BuildDetails: Codable, Equatable, Sendable {
    public var sourcePr: String?
    public var sourceCommit: String?
    public var builtUtc: String?
    public var releaseTag: String?

    public init(sourcePr: String? = nil, sourceCommit: String? = nil, builtUtc: String? = nil, releaseTag: String? = nil) {
        self.sourcePr = Self.clean(sourcePr, #"^[1-9][0-9]{0,8}$"#)
        self.sourceCommit = Self.clean(sourceCommit, #"^[0-9a-fA-F]{7,40}$"#)?.lowercased()
        self.builtUtc = Self.clean(builtUtc, #"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"#)
        self.releaseTag = Self.clean(releaseTag, #"^mac-v[0-9]+\.[0-9]+\.[0-9]+(-beta[0-9]{3})?$"#)
    }

    public init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(sourcePr: try values.decodeIfPresent(String.self, forKey: .sourcePr),
                  sourceCommit: try values.decodeIfPresent(String.self, forKey: .sourceCommit),
                  builtUtc: try values.decodeIfPresent(String.self, forKey: .builtUtc),
                  releaseTag: try values.decodeIfPresent(String.self, forKey: .releaseTag))
    }

    private static func clean(_ value: String?, _ pattern: String) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespacesAndNewlines), !value.isEmpty,
              value.range(of: pattern, options: .regularExpression) != nil else { return nil }
        return value
    }

    public static func load(from url: URL?) -> BuildDetails {
        guard let url, let data = try? Data(contentsOf: url),
              let details = try? JSONDecoder().decode(BuildDetails.self, from: data) else { return BuildDetails() }
        return details
    }
}

/// What the Mac Info window says about this copy. Pure logic, unit tested; the SwiftUI view only shows it.
public struct BuildInfo: Equatable, Sendable {
    public static let localBuild = "Local build"
    public static let appName = "Evict for Mac"
    /// The full release version, e.g. "1.2.0" or "1.2.0-beta003" (Info.plist EvictReleaseVersion).
    public let version: String
    public let buildNumber: String
    public let details: BuildDetails
    public let installedAs: String

    public init(version: String, buildNumber: String = Version.build, details: BuildDetails, installedAs: String) {
        self.version = version
        self.buildNumber = buildNumber
        self.details = details
        self.installedAs = installedAs
    }

    public var isBeta: Bool { version.contains("-") }
    public var channel: String { isBeta ? "Beta" : "Stable" }
    /// "1.2.0" of "1.2.0-beta003".
    public var coreVersion: String { String(version.split(separator: "-", maxSplits: 1).first ?? Substring(version)) }
    /// "003" of a short beta; nil for stable, old long and unreserved (000) versions.
    public var betaNumber: String? {
        guard let range = version.range(of: #"-beta[0-9]{3}$"#, options: .regularExpression) else { return nil }
        let number = String(version[range].dropFirst(5))
        return number == "000" ? nil : number
    }
    public var isLocalBuild: Bool { details.sourceCommit == nil }
    public var subtitle: String { "Version \(version) · \(channel) · Mac" }

    public var betaBanner: String? {
        guard isBeta else { return nil }
        if let number = betaNumber {
            return "This is a test build. It is beta \(number) of \(Self.appName) \(coreVersion), made from a pull request."
        }
        return "This is a test build of \(Self.appName) \(coreVersion), made from a pull request."
    }

    public var pullRequestURL: URL? {
        details.sourcePr.flatMap { URL(string: "https://github.com/krishnabhunia/evict-uninstaller/pull/\($0)") }
    }
    public var commitText: String { details.sourceCommit.map { String($0.prefix(7)) } ?? Self.localBuild }
    public var downloadName: String { "Evict-\(version)-macOS-universal.dmg" }

    public var builtDate: Date? {
        details.builtUtc.flatMap { ISO8601DateFormatter().date(from: $0) }
    }

    /// "04-Nov-2026, 19:55" in the given (default: this Mac's) time zone.
    public func builtOnText(timeZone: TimeZone = .current) -> String {
        guard let date = builtDate else { return Self.localBuild }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = timeZone
        formatter.dateFormat = "dd-MMM-yyyy, HH:mm"
        return formatter.string(from: date)
    }

    /// The "SrNo. | Item | This copy" table, in display order.
    public func rows(timeZone: TimeZone = .current) -> [(item: String, value: String)] {
        var rows: [(item: String, value: String)] = [("Version", "\(version) (build \(buildNumber))")]
        rows.append(("Channel", channel))
        if let pr = details.sourcePr { rows.append(("Made from", "PR #\(pr)")) }
        rows.append(("Commit", commitText))
        rows.append(("Built on", builtOnText(timeZone: timeZone)))
        rows.append(("GitHub tag", details.releaseTag ?? (isLocalBuild ? Self.localBuild : "Not published")))
        rows.append(("Download", downloadName))
        rows.append(("Installed as", installedAs))
        return rows
    }

    /// Two lines for bug reports.
    public func copyText(system: String) -> String {
        var first = ["\(Self.appName) \(version) (\(channel))"]
        if let pr = details.sourcePr { first.append("PR #\(pr)") }
        first.append(details.sourceCommit == nil ? "local build" : commitText)
        if let date = builtDate {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "en_US_POSIX")
            formatter.timeZone = TimeZone(identifier: "UTC")
            formatter.dateFormat = "yyyy-MM-dd HH:mm"
            first.append("built \(formatter.string(from: date)) UTC")
        }
        return first.joined(separator: " · ") + "\n" + [system, installedAs].joined(separator: " · ")
    }

    public static func describeInstall(bundlePath: String) -> String {
        if bundlePath.hasPrefix("/Applications/") { return "app in /Applications" }
        if bundlePath.hasPrefix(NSHomeDirectory() + "/Applications/") { return "app in ~/Applications" }
        return "app at \(bundlePath)"
    }
}

/// Parsing for the Info window's embedded documents.
public enum InfoContent {
    public static let featureHeader = ["SrNo.", "Area", "What it does", "Since", "Windows", "Mac"]

    /// Rows of the feature table; malformed rows are skipped so the window always opens.
    public static func parseFeatures(_ markdown: String?) -> [FeatureRow] {
        guard let markdown else { return [] }
        var rows: [FeatureRow] = []
        var inTable = false
        for raw in markdown.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard line.count >= 2, line.hasPrefix("|"), line.hasSuffix("|") else {
                if inTable { break }
                continue
            }
            let cells = line.dropFirst().dropLast().components(separatedBy: "|")
                .map { $0.trimmingCharacters(in: .whitespaces) }
            if !inTable {
                inTable = cells == featureHeader
                continue
            }
            if cells.allSatisfy({ $0.range(of: #"^:?-{3,}:?$"#, options: .regularExpression) != nil }) { continue }
            guard cells.count == featureHeader.count, let number = Int(cells[0]),
                  cells[3].range(of: #"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"#, options: .regularExpression) != nil
            else { continue }
            rows.append(FeatureRow(srNo: number, area: cells[1], whatItDoes: cells[2], since: cells[3],
                                   onWindows: cells[4] == "Yes", onMac: cells[5] == "Yes"))
        }
        return rows
    }

    /// Mac rows matching every search word. "Since" uses the Windows release numbers, so the Mac app,
    /// whose versions are independent, offers no "New in" filter.
    public static func filter(_ rows: [FeatureRow], query: String) -> [FeatureRow] {
        let words = query.split(separator: " ").map(String.init)
        return rows.filter { row in
            row.onMac
                && words.allSatisfy { row.area.localizedCaseInsensitiveContains($0) || row.whatItDoes.localizedCaseInsensitiveContains($0) }
        }
    }

    /// The `## <version>` section without the generator's HTML comment and Markdown emphasis.
    public static func changelogSection(_ markdown: String?, version: String?) -> ChangelogSection? {
        guard let markdown, let version, !version.isEmpty else { return nil }
        let lines = markdown.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        for (index, line) in lines.enumerated() {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            guard trimmed.hasPrefix("## ") else { continue }
            let title = String(trimmed.dropFirst(3)).trimmingCharacters(in: .whitespaces)
            guard title == version || title.hasPrefix(version + " ") else { continue }
            var body: [String] = []
            for next in lines[(index + 1)...] {
                if next.hasPrefix("## ") { break }
                var text = next.replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
                if text.trimmingCharacters(in: .whitespaces).range(of: #"^<!--.*-->$"#, options: .regularExpression) != nil { continue }
                text = text.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: "")
                text = text.replacingOccurrences(of: #"^(\s*)[-*]\s+"#, with: "$1• ", options: .regularExpression)
                text = text.replacingOccurrences(of: #"^###\s+"#, with: "", options: .regularExpression)
                if text.isEmpty && (body.last?.isEmpty ?? true) { continue }
                body.append(text)
            }
            while body.last?.isEmpty == true { body.removeLast() }
            return ChangelogSection(heading: title, lines: body)
        }
        return nil
    }
}
