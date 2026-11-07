import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Native Mac versions have their own release stream; a Windows release is never a Mac update.
public struct MacReleaseVersion: Hashable, Comparable, Codable, Sendable, CustomStringConvertible {
    public let rawValue: String
    public let major: Int
    public let minor: Int
    public let patch: Int
    public let beta: [Int]?

    public init?(_ value: String) {
        let pattern = #"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta([0-9]{3})|-beta(?:\.([0-9]+(?:\.[0-9]+)*))?)?$"#
        guard value.utf8.count <= 100,
              let expression = try? NSRegularExpression(pattern: pattern),
              let match = expression.firstMatch(in: value, range: NSRange(value.startIndex..., in: value)),
              match.range.length == value.utf16.count else { return nil }
        func text(_ index: Int) -> String? {
            guard let range = Range(match.range(at: index), in: value) else { return nil }
            return String(value[range])
        }
        guard let major = text(1).flatMap(Int.init), let minor = text(2).flatMap(Int.init),
              let patch = text(3).flatMap(Int.init),
              [major, minor, patch].allSatisfy({ $0 <= Int(Int32.max) }) else { return nil }
        let suffix = text(4) ?? text(5)
        if text(4) == "000" { return nil }
        let values = suffix?.split(separator: ".").compactMap { Int($0) }
        if let suffix, values?.count != suffix.split(separator: ".").count { return nil }
        self.rawValue = value
        self.major = major
        self.minor = minor
        self.patch = patch
        self.beta = value.contains("-beta") ? values ?? [0] : nil
    }

    public var description: String { rawValue }
    public var core: String { "\(major).\(minor).\(patch)" }
    public var isBeta: Bool { beta != nil }

    public static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.major == rhs.major && lhs.minor == rhs.minor && lhs.patch == rhs.patch && lhs.beta == rhs.beta
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(major); hasher.combine(minor); hasher.combine(patch); hasher.combine(beta)
    }

    public static func < (lhs: Self, rhs: Self) -> Bool {
        let left = [lhs.major, lhs.minor, lhs.patch], right = [rhs.major, rhs.minor, rhs.patch]
        if left != right { return left.lexicographicallyPrecedes(right) }
        switch (lhs.beta, rhs.beta) {
        case (nil, nil): return false
        case (.some, nil): return true
        case (nil, .some): return false
        case let (.some(a), .some(b)): return a.lexicographicallyPrecedes(b)
        }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = try container.decode(String.self)
        guard let value = Self(raw) else {
            throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid native Mac release version")
        }
        self = value
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

public struct MacReleaseAsset: Codable, Equatable, Sendable {
    public let name: String
    public let url: URL
    public let size: Int64

    public init(name: String, url: URL, size: Int64) {
        self.name = name; self.url = url; self.size = size
    }
}

public struct MacRelease: Identifiable, Equatable, Sendable {
    public let tag: String
    public let version: MacReleaseVersion
    public let title: String
    public let notes: String
    public let publishedAt: Date?
    public let archive: MacReleaseAsset
    public let checksum: MacReleaseAsset
    public var id: String { tag }
    public var isBeta: Bool { version.isBeta }
    public var pageURL: URL { URL(string: "https://github.com/krishnabhunia/evict-uninstaller/releases/tag/\(tag)")! }

    public init(tag: String, version: MacReleaseVersion, title: String, notes: String,
                publishedAt: Date? = nil, archive: MacReleaseAsset, checksum: MacReleaseAsset) {
        self.tag = tag; self.version = version; self.title = title; self.notes = notes
        self.publishedAt = publishedAt; self.archive = archive; self.checksum = checksum
    }
}

public enum MacReleaseError: LocalizedError, Sendable {
    case invalid(String)
    case network(String)
    case file(String)

    public var errorDescription: String? {
        switch self {
        case .invalid(let message), .network(let message), .file(let message): return message
        }
    }
}

public enum MacReleasePolicy {
    public static let repository = "krishnabhunia/evict-uninstaller"
    public static let maximumArchiveBytes: Int64 = 512 * 1024 * 1024

    /// Match the exact repository, immutable native tag and asset, not a similar-looking URL.
    public static func isReleaseAssetURL(_ url: URL, tag: String, assetName: String) -> Bool {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.scheme == "https", components.host == "github.com", components.port == nil,
              components.user == nil, components.password == nil, components.query == nil, components.fragment == nil,
              tag.hasPrefix("mac-v"), let version = MacReleaseVersion(String(tag.dropFirst(5))),
              tag == "mac-v" + version.rawValue,
              !assetName.contains("/"), !assetName.contains("\\"),
              components.path == "/\(repository)/releases/download/\(tag)/\(assetName)" else { return false }
        return true
    }

    /// GitHub serves asset bytes from these HTTPS hosts after its authenticated redirect.
    static func permitsRedirect(_ url: URL, initialURL: URL) -> Bool {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.scheme == "https", components.port == nil,
              components.user == nil, components.password == nil, components.fragment == nil else { return false }
        if components.host == "github.com" { return url == initialURL }
        return ["release-assets.githubusercontent.com", "objects.githubusercontent.com"].contains(components.host ?? "")
    }

    public static func sorted(_ releases: [MacRelease]) -> [MacRelease] {
        releases.sorted {
            if $0.version != $1.version { return $0.version > $1.version }
            return ($0.publishedAt ?? .distantPast) > ($1.publishedAt ?? .distantPast)
        }
    }

    public static func visible(_ releases: [MacRelease], includeBeta: Bool, showAll: Bool) -> [MacRelease] {
        let available = sorted(releases.filter { includeBeta || !$0.isBeta })
        if showAll { return available }
        var channels = Set<Bool>()
        return available.filter { channels.insert($0.isBeta).inserted }
    }

    public static func availableUpdate(_ releases: [MacRelease], current: String,
                                       preferences: MacReleasePreferences) -> MacRelease? {
        guard preferences.pinnedVersion == nil, let running = MacReleaseVersion(current) else { return nil }
        return sorted(releases).first {
            $0.version > running && (preferences.includeBeta || !$0.isBeta) &&
                !preferences.skippedTags.contains($0.tag)
        }
    }
}

/// The GitHub response is decoded independently of network transport so release rules are testable.
public enum MacReleaseCatalog {
    private struct GitHubAsset: Decodable {
        let name: String
        let browser_download_url: URL
        let size: Int64
    }

    private struct GitHubRelease: Decodable {
        let tag_name: String
        let name: String?
        let body: String?
        let draft: Bool
        let prerelease: Bool
        let published_at: String?
        let assets: [GitHubAsset]
    }

    public static func decode(_ data: Data) throws -> [MacRelease] {
        let entries = try JSONDecoder().decode([GitHubRelease].self, from: data)
        let dates = ISO8601DateFormatter()
        var tags = Set<String>()
        return MacReleasePolicy.sorted(entries.compactMap { entry in
            guard !entry.draft, entry.tag_name.hasPrefix("mac-v"),
                  let version = MacReleaseVersion(String(entry.tag_name.dropFirst(5))),
                  entry.prerelease == version.isBeta,
                  tags.insert(entry.tag_name).inserted else { return nil }
            let nativeName = "Evict-\(version.rawValue)-macOS-universal.zip"
            let legacyName = "Evict-\(version.rawValue).zip"
            // A legacy native ZIP is accepted only when its tag carries the same native version.
            let candidate = entry.assets.first { $0.name == nativeName } ?? entry.assets.first { $0.name == legacyName }
            guard let zip = candidate, zip.size > 0, zip.size <= MacReleasePolicy.maximumArchiveBytes,
                  let sum = entry.assets.first(where: { $0.name == zip.name + ".sha256" }),
                  sum.size > 0, sum.size <= 8192,
                  MacReleasePolicy.isReleaseAssetURL(zip.browser_download_url, tag: entry.tag_name, assetName: zip.name),
                  MacReleasePolicy.isReleaseAssetURL(sum.browser_download_url, tag: entry.tag_name, assetName: sum.name)
            else { return nil }
            return MacRelease(tag: entry.tag_name, version: version, title: entry.name ?? "Evict \(version)",
                              notes: entry.body ?? "", publishedAt: entry.published_at.flatMap(dates.date(from:)),
                              archive: MacReleaseAsset(name: zip.name, url: zip.browser_download_url, size: zip.size),
                              checksum: MacReleaseAsset(name: sum.name, url: sum.browser_download_url, size: sum.size))
        })
    }
}

private final class MacReleaseRedirectGate: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    let initialURL: URL
    let asset: Bool
    init(initialURL: URL, asset: Bool) { self.initialURL = initialURL; self.asset = asset }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        guard asset, let url = request.url, MacReleasePolicy.permitsRedirect(url, initialURL: initialURL) else {
            completionHandler(nil); return
        }
        completionHandler(request)
    }
}

public struct MacReleaseClient: Sendable {
    public init() {}

    public func fetchCatalog() async throws -> [MacRelease] {
        var releases: [MacRelease] = []
        var ids = Set<String>()
        for page in 1...10 {
            try Task.checkCancellation()
            let url = URL(string: "https://api.github.com/repos/\(MacReleasePolicy.repository)/releases?per_page=100&page=\(page)")!
            var request = request(for: url)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            request.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
            let session = URLSession(configuration: .ephemeral)
            defer { session.invalidateAndCancel() }
            let (data, response) = try await session.data(for: request, delegate: MacReleaseRedirectGate(initialURL: url, asset: false))
            try validate(response, expected: url, asset: false)
            guard data.count <= 8 * 1024 * 1024 else { throw MacReleaseError.invalid("The release catalog is too large.") }
            let decoded = try MacReleaseCatalog.decode(data)
            releases.append(contentsOf: decoded.filter { ids.insert($0.id).inserted })
            let count = (try JSONSerialization.jsonObject(with: data) as? [Any])?.count ?? 0
            if count < 100 { return MacReleasePolicy.sorted(releases) }
        }
        // Ten pages is an explicit bounded history window, not a claim that no older release exists.
        return MacReleasePolicy.sorted(releases)
    }

    public func download(_ release: MacRelease, into directory: URL) async throws -> URL {
        let nativeName = "Evict-\(release.version.rawValue)-macOS-universal.zip"
        let legacyName = "Evict-\(release.version.rawValue).zip"
        guard release.tag == "mac-v" + release.version.rawValue,
              [nativeName, legacyName].contains(release.archive.name),
              release.checksum.name == release.archive.name + ".sha256",
              MacReleasePolicy.isReleaseAssetURL(release.archive.url, tag: release.tag, assetName: release.archive.name),
              MacReleasePolicy.isReleaseAssetURL(release.checksum.url, tag: release.tag, assetName: release.checksum.name),
              release.archive.size > 0, release.archive.size <= MacReleasePolicy.maximumArchiveBytes,
              release.checksum.size > 0, release.checksum.size <= 8192 else {
            throw MacReleaseError.invalid("This release does not contain an allowed native macOS download.")
        }
        let session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let (sumData, sumResponse) = try await session.data(for: request(for: release.checksum.url),
                                                        delegate: MacReleaseRedirectGate(initialURL: release.checksum.url, asset: true))
        try validate(sumResponse, expected: release.checksum.url, asset: true)
        guard sumData.count > 0, sumData.count <= 8192, Int64(sumData.count) == release.checksum.size else {
            throw MacReleaseError.invalid("The checksum download has an unexpected size.")
        }
        let digest = try MacReleaseIntegrity.expectedDigest(sumData, assetName: release.archive.name)
        try Task.checkCancellation()
        let (temporaryURL, response) = try await session.download(for: request(for: release.archive.url),
                                                                delegate: MacReleaseRedirectGate(initialURL: release.archive.url, asset: true))
        defer { try? FileManager.default.removeItem(at: temporaryURL) }
        try validate(response, expected: release.archive.url, asset: true)
        let actualSize = (try FileManager.default.attributesOfItem(atPath: temporaryURL.path)[.size] as? NSNumber)?.int64Value ?? -1
        guard actualSize == release.archive.size else { throw MacReleaseError.invalid("The download is incomplete or its size has changed.") }
        try Task.checkCancellation()
        guard try MacReleaseIntegrity.sha256(of: temporaryURL) == digest else {
            throw MacReleaseError.invalid("The download's SHA-256 checksum does not match. Installation was stopped.")
        }
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let destination = directory.appendingPathComponent(release.archive.name)
        guard !FileManager.default.fileExists(atPath: destination.path) else {
            throw MacReleaseError.file("A file already exists in this download location.")
        }
        try FileManager.default.moveItem(at: temporaryURL, to: destination)
        return destination
    }

    private func request(for url: URL) -> URLRequest {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 120)
        request.setValue("Evict-macOS-Version-Manager", forHTTPHeaderField: "User-Agent")
        return request
    }

    private func validate(_ response: URLResponse, expected: URL, asset: Bool) throws {
        guard let http = response as? HTTPURLResponse, let finalURL = response.url,
              finalURL == expected || (asset && MacReleasePolicy.permitsRedirect(finalURL, initialURL: expected)) else {
            throw MacReleaseError.network("The update server redirected to an unexpected location.")
        }
        guard http.statusCode == 200 else {
            if [403, 429].contains(http.statusCode) {
                throw MacReleaseError.network("GitHub has temporarily limited update requests. Try again later.")
            }
            throw MacReleaseError.network("GitHub returned HTTP \(http.statusCode). Your installed version has not changed.")
        }
    }
}
