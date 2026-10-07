import Foundation

/// Removes files – always by moving them to the Trash, never by deleting them.
///
/// Two rules hold for every path that reaches this type:
/// 1. `SafePaths` must allow it (including revalidated ownership for a command-line link).
/// 2. After the move, the path is read again; if it is still there the item is reported as failed
///    instead of counted as removed. (This mirrors the registry verification added to the Windows app.)
public struct Remover: Sendable {

    public enum RemovalError: LocalizedError {
        case refused(String)
        case needsAdmin
        case notFound
        case system(String)

        public var errorDescription: String? {
            switch self {
            case .refused(let reason): return "Not removed – \(reason)"
            case .needsAdmin: return "Administrator rights are required for this location"
            case .notFound: return "The item is already missing; nothing was moved to the Trash"
            case .system(let message): return message
            }
        }
    }

    private let moveToTrash: @Sendable (String) throws -> Void

    public init() {
        moveToTrash = { path in
            #if os(macOS)
            try FileManager.default.trashItem(at: URL(fileURLWithPath: path), resultingItemURL: nil)
            #else
            throw RemovalError.system("Moving files to the Trash is only available on macOS")
            #endif
        }
    }

    /// Isolated tests can move their fixtures to a temporary folder without using the real Trash.
    init(moveToTrash: @escaping @Sendable (String) throws -> Void) { self.moveToTrash = moveToTrash }

    static func entryExists(_ path: String) -> Bool {
        FileManager.default.fileExists(atPath: path) ||
            (try? FileManager.default.destinationOfSymbolicLink(atPath: path)) != nil
    }

    /// Moves one path to the Trash and verifies it is gone.
    public func trash(_ path: String, ownedBundlePath: String? = nil) throws -> Int64 {
        let verdict = ownedBundlePath.map { SafePaths.checkOwnedBinaryLink(path, bundlePath: $0) } ?? SafePaths.check(path)
        if case .refused(let reason) = verdict { throw RemovalError.refused(reason) }

        // A link is an entry, not the data in its destination. Never walk the app bundle
        // again when removing its command-line shortcut.
        let isLink = (try? FileManager.default.destinationOfSymbolicLink(atPath: path)) != nil
        let size = isLink ? 0 : FileSize.measure(path)
        guard Self.entryExists(path) else { throw RemovalError.notFound }

        do {
            try moveToTrash(path)
        } catch let error as NSError {
            if error.code == NSFileWriteNoPermissionError || error.code == NSFileWriteVolumeReadOnlyError {
                throw SafePaths.needsAdmin(path) ? RemovalError.needsAdmin : RemovalError.system(error.localizedDescription)
            }
            throw RemovalError.system(error.localizedDescription)
        }
        // Verified, not assumed: if the path is still there the caller must not be told it was removed.
        if Self.entryExists(path) {
            throw RemovalError.system("Still present after being moved to the Trash")
        }
        return size
    }

    /// Removes a list of leftovers, reporting progress as it goes.
    public func remove(_ items: [LeftoverItem], progress: ((Double, String) -> Void)? = nil) -> RemovalResult {
        var result = RemovalResult()
        for (index, item) in items.enumerated() {
            progress?(Double(index) / Double(max(items.count, 1)), item.displayName)

            if item.receiptIdentifier != nil {
                // Receipts are database entries, not files; forgetting one needs an admin prompt,
                // so Evict only reports it and leaves the actual `pkgutil --forget` to the user.
                result.needsAdminCount += 1
                result.failed.append((path: item.path, error: "Receipt – run: sudo pkgutil --forget \(item.receiptIdentifier!)"))
                continue
            }
            do {
                let freed = try trash(item.path, ownedBundlePath: item.ownedBundlePath)
                result.trashed.append(item.path)
                result.bytesFreed += freed
            } catch RemovalError.needsAdmin {
                result.needsAdminCount += 1
                result.failed.append((path: item.path, error: RemovalError.needsAdmin.localizedDescription))
            } catch {
                if Self.entryExists(item.path) { result.stillPresent.append(item.path) }
                result.failed.append((path: item.path, error: error.localizedDescription))
                Log.warn("Could not remove \(item.path): \(error.localizedDescription)")
            }
        }
        progress?(1, "")
        return result
    }
}
