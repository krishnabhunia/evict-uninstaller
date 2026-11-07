import Foundation

public enum MacCleanupCategory: String, CaseIterable, Codable, Sendable {
    case caches = "Application caches"
    case logs = "Application logs"
}

public struct MacCleanupCandidate: Identifiable, Equatable, Sendable {
    public var id: String { path }
    public let path: String
    public let root: String
    public let category: MacCleanupCategory
    public let sizeBytes: Int64
    public let sizeIsEstimate: Bool
    public let modified: Date?
    let fileNumber: UInt64?

    public var name: String { (path as NSString).lastPathComponent }
    public var reviewNote: String {
        category == .caches
            ? "Close the owning application first. It may recreate this cache."
            : "Review before removing. Logs can be useful when diagnosing a problem."
    }
}

public struct MacCleanupScan: Sendable {
    public let candidates: [MacCleanupCandidate]
    public let warnings: [String]
    public let date: Date
    public var totalBytes: Int64 { candidates.reduce(0) { $0 + $1.sizeBytes } }
}

/// Every item is reviewed and unchecked. Only direct children of explicitly named cache/log
/// roots are considered; neither the shared root nor a symlink is a cleanup candidate.
public struct MacCleanupScanner: Sendable {
    struct Root: Sendable {
        let path: String
        let category: MacCleanupCategory
    }

    private let roots: [Root]
    private let acceptsPath: @Sendable (String) -> Bool
    private let fileLimit: Int

    public init() {
        roots = [Root(path: AppPaths.userLibrary + "/Caches", category: .caches),
                 Root(path: AppPaths.userLibrary + "/Logs", category: .logs)]
        acceptsPath = { SafePaths.check($0).isAllowed }
        fileLimit = 20_000
    }

    /// Fixture-only dependency injection. Production always uses SafePaths and user Library roots.
    init(roots: [Root], fileLimit: Int = 20_000, acceptsPath: @escaping @Sendable (String) -> Bool) {
        self.roots = roots
        self.fileLimit = fileLimit
        self.acceptsPath = acceptsPath
    }

    public func scan() -> MacCleanupScan {
        let fm = FileManager.default
        var candidates: [MacCleanupCandidate] = []
        var warnings: [String] = []
        var remainingEntries = 40_000
        for root in roots {
            if remainingEntries <= 0 { break }
            guard fm.fileExists(atPath: root.path) else { continue }
            guard MacMaintenancePaths.isUnaliased(root.path) else {
                warnings.append("Skipped a linked cleanup folder: \(root.path)")
                continue
            }
            do {
                let children = try fm.contentsOfDirectory(atPath: root.path).sorted()
                for child in children where !child.hasPrefix(".") && !child.hasPrefix("com.apple.") {
                    if remainingEntries <= 0 || candidates.count >= 500 {
                        warnings.append("The cleanup scan reached its size limit. Results cover only the inspected items.")
                        remainingEntries = 0
                        break
                    }
                    let path = root.path + "/" + child
                    guard acceptsPath(path), MacMaintenancePaths.isDirectUnaliasedChild(path, root: root.path),
                          let attributes = try? fm.attributesOfItem(atPath: path),
                          let type = attributes[.type] as? FileAttributeType,
                          type == .typeDirectory || type == .typeRegular else { continue }
                    let measured = Self.measure(path, limit: min(fileLimit, remainingEntries))
                    remainingEntries -= measured.visited
                    guard measured.bytes > 0 else { continue }
                    candidates.append(MacCleanupCandidate(path: path, root: root.path, category: root.category,
                        sizeBytes: measured.bytes, sizeIsEstimate: measured.estimated,
                        modified: attributes[.modificationDate] as? Date,
                        fileNumber: (attributes[.systemFileNumber] as? NSNumber)?.uint64Value))
                }
            } catch {
                warnings.append("Could not inspect \(root.path): \(error.localizedDescription)")
            }
        }
        if remainingEntries <= 0 && !warnings.contains(where: { $0.contains("size limit") }) {
            warnings.append("The cleanup scan reached its size limit. Results cover only the inspected items.")
        }
        return MacCleanupScan(candidates: candidates.sorted { $0.sizeBytes > $1.sizeBytes }, warnings: warnings, date: Date())
    }

    /// Recheck ownership, identity and aliases immediately before invoking the existing Trash gate.
    public func validate(_ item: MacCleanupCandidate) -> Bool {
        guard roots.contains(where: { $0.path == item.root && $0.category == item.category }),
              acceptsPath(item.path), MacMaintenancePaths.isDirectUnaliasedChild(item.path, root: item.root),
              let attributes = try? FileManager.default.attributesOfItem(atPath: item.path) else { return false }
        let fileNumber = (attributes[.systemFileNumber] as? NSNumber)?.uint64Value
        let modified = attributes[.modificationDate] as? Date
        return fileNumber == item.fileNumber && modified == item.modified
    }

    public func moveToTrash(_ selected: [MacCleanupCandidate]) -> RemovalResult {
        var result = RemovalResult()
        var seen = Set<String>()
        let remover = Remover()
        for item in selected where seen.insert(item.path).inserted {
            guard validate(item) else {
                result.failed.append((item.path, "The item changed or is no longer eligible. Scan and review it again."))
                continue
            }
            do {
                let bytes = try remover.trash(item.path)
                result.trashed.append(item.path)
                result.bytesFreed += bytes
            } catch {
                result.failed.append((item.path, error.localizedDescription))
            }
        }
        return result
    }

    static func measure(_ path: String, limit: Int) -> (bytes: Int64, estimated: Bool, visited: Int) {
        let fm = FileManager.default
        guard let attributes = try? fm.attributesOfItem(atPath: path) else { return (0, true, 1) }
        if attributes[.type] as? FileAttributeType == .typeRegular {
            return ((attributes[.size] as? NSNumber)?.int64Value ?? 0, false, 1)
        }
        guard let walker = fm.enumerator(at: URL(fileURLWithPath: path),
                                        includingPropertiesForKeys: [.isSymbolicLinkKey],
                                        options: [.skipsHiddenFiles]) else { return (0, true, 1) }
        var bytes: Int64 = 0
        var count = 0
        for case let url as URL in walker {
            count += 1
            if count > limit { return (bytes, true, count) }
            guard let info = try? fm.attributesOfItem(atPath: url.path) else { continue }
            if info[.type] as? FileAttributeType == .typeSymbolicLink {
                walker.skipDescendants()
                continue
            }
            if info[.type] as? FileAttributeType == .typeRegular {
                bytes += (info[.size] as? NSNumber)?.int64Value ?? 0
            }
        }
        return (bytes, false, max(1, count))
    }
}

enum MacMaintenancePaths {
    static func isUnaliased(_ path: String) -> Bool {
        let url = URL(fileURLWithPath: path).standardizedFileURL
        guard path.hasPrefix("/"), !path.split(separator: "/").contains("..") else { return false }
        return url.path == url.resolvingSymlinksInPath().standardizedFileURL.path
    }

    static func isDirectUnaliasedChild(_ path: String, root: String) -> Bool {
        guard isUnaliased(root), isUnaliased(path) else { return false }
        let url = URL(fileURLWithPath: path).standardizedFileURL
        return url.deletingLastPathComponent().path == URL(fileURLWithPath: root).standardizedFileURL.path &&
            url.path != URL(fileURLWithPath: root).standardizedFileURL.path
    }
}

public enum MacApplicationFilter: String, CaseIterable, Identifiable, Sendable {
    case all = "All"
    case recent = "Recently installed"
    case large = "Large"
    case rarelyUsed = "Rarely used"
    case appStore = "App Store"
    case homebrew = "Homebrew"
    public var id: String { rawValue }

    public func includes(_ app: InstalledApp, now: Date = Date()) -> Bool {
        switch self {
        case .all: return true
        case .recent:
            guard let date = app.installedDate else { return false }
            return date <= now && date >= now.addingTimeInterval(-30 * 86_400)
        case .large: return app.sizeBytes >= 1_073_741_824
        case .rarelyUsed:
            guard let date = app.lastUsedDate else { return false }
            return date < now.addingTimeInterval(-90 * 86_400)
        case .appStore: return app.source == .appStore
        case .homebrew: return app.source == .homebrewCask
        }
    }
}
