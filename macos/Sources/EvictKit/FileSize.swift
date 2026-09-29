import Foundation

/// Directory sizes, measured with a hard cap so a huge folder cannot stall the UI.
public enum FileSize {

    /// Size of a file, or the total of a folder's contents. Symlinks are never followed.
    public static func measure(_ path: String, fileLimit: Int = 40_000) -> Int64 {
        let fm = FileManager.default
        var isDirectory: ObjCBool = false
        guard fm.fileExists(atPath: path, isDirectory: &isDirectory) else { return 0 }
        if !isDirectory.boolValue { return fileBytes(path) }

        var total: Int64 = 0
        var visited = 0
        let url = URL(fileURLWithPath: path)
        guard let walker = fm.enumerator(at: url,
                                         includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isRegularFileKey],
                                         options: [.skipsHiddenFiles]) else { return 0 }
        for case let child as URL in walker {
            visited += 1
            if visited > fileLimit { break }
            let values = try? child.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey, .isRegularFileKey])
            guard values?.isRegularFile == true else { continue }
            let bytes = values?.totalFileAllocatedSize ?? values?.fileAllocatedSize ?? 0
            total += Int64(bytes)
        }
        return total
    }

    public static func fileBytes(_ path: String) -> Int64 {
        let attributes = try? FileManager.default.attributesOfItem(atPath: path)
        return (attributes?[.size] as? NSNumber)?.int64Value ?? 0
    }

    public static func modified(_ path: String) -> Date? {
        let attributes = try? FileManager.default.attributesOfItem(atPath: path)
        return attributes?[.modificationDate] as? Date
    }

    public static func created(_ path: String) -> Date? {
        let attributes = try? FileManager.default.attributesOfItem(atPath: path)
        return attributes?[.creationDate] as? Date
    }
}
