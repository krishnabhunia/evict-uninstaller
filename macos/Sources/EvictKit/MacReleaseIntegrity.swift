import Foundation
#if canImport(CryptoKit)
import CryptoKit
#endif

public enum MacReleaseIntegrity {
    /// Checksum files must identify this exact asset. Another file's digest is never reused.
    public static func expectedDigest(_ data: Data, assetName: String) throws -> String {
        guard data.count <= 8192, let text = String(data: data, encoding: .utf8),
              !assetName.contains("/"), !assetName.contains("\\") else {
            throw MacReleaseError.invalid("The release checksum is not a supported SHA-256 manifest.")
        }
        let expression = try NSRegularExpression(pattern: #"^([a-fA-F0-9]{64})[ \t]+\*?(.+)$"#)
        var matches: [String] = []
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = String(raw).trimmingCharacters(in: .whitespaces)
            guard let match = expression.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)),
                  let digestRange = Range(match.range(at: 1), in: line),
                  let nameRange = Range(match.range(at: 2), in: line) else { continue }
            if String(line[nameRange]) == assetName { matches.append(String(line[digestRange]).lowercased()) }
        }
        guard matches.count == 1 else {
            throw MacReleaseError.invalid("The checksum manifest must contain exactly one entry for \(assetName).")
        }
        return matches[0]
    }

    public static func sha256(of url: URL) throws -> String {
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else {
            throw MacReleaseError.file("The file to verify is not a regular file.")
        }
        #if canImport(CryptoKit)
        let file = try FileHandle(forReadingFrom: url)
        defer { try? file.close() }
        var digest = SHA256()
        while let data = try file.read(upToCount: 1024 * 1024), !data.isEmpty { digest.update(data: data) }
        return digest.finalize().map { String(format: "%02x", $0) }.joined()
        #else
        // Linux test hosts do not ship CryptoKit. The application uses Apple's CryptoKit above.
        let output = Shell.run("/usr/bin/sha256sum", [url.path], timeout: 120)
        guard output.ok, let digest = output.stdout.split(whereSeparator: \.isWhitespace).first,
              digest.count == 64, digest.allSatisfy({ $0.isHexDigit }) else {
            throw MacReleaseError.file("The SHA-256 verifier is unavailable.")
        }
        return digest.lowercased()
        #endif
    }
}

/// Validate the complete ZIP directory before giving it to ditto. A signed download can still
/// be malformed, and extraction must never follow a path or link outside its fresh staging folder.
public enum MacReleaseArchive {
    public struct Contents: Equatable, Sendable {
        public let paths: [String]
        public let expandedBytes: Int64
    }

    public static func inspect(_ archive: URL) throws -> Contents {
        let size = (try FileManager.default.attributesOfItem(atPath: archive.path)[.size] as? NSNumber)?.int64Value ?? 0
        guard size > 0, size <= MacReleasePolicy.maximumArchiveBytes else {
            throw MacReleaseError.invalid("The native application archive has an unsupported size.")
        }
        return try inspect(Data(contentsOf: archive, options: .mappedIfSafe))
    }

    public static func inspect(_ data: Data) throws -> Contents {
        func invalid(_ message: String) -> MacReleaseError { .invalid("The application archive is unsafe: " + message) }
        func u16(_ offset: Int) throws -> UInt16 {
            guard offset >= 0, offset <= data.count - 2 else { throw invalid("truncated ZIP header.") }
            return UInt16(data[offset]) | UInt16(data[offset + 1]) << 8
        }
        func u32(_ offset: Int) throws -> UInt32 {
            UInt32(try u16(offset)) | UInt32(try u16(offset + 2)) << 16
        }
        guard data.count >= 22, data.count <= Int(MacReleasePolicy.maximumArchiveBytes) else {
            throw invalid("invalid ZIP length.")
        }
        var end: Int?
        for offset in stride(from: data.count - 22, through: max(0, data.count - 65_557), by: -1) {
            if try u32(offset) == 0x06054b50, offset + 22 + Int(try u16(offset + 20)) == data.count {
                end = offset; break
            }
        }
        guard let end, try u16(end + 4) == 0, try u16(end + 6) == 0,
              try u16(end + 8) == u16(end + 10) else { throw invalid("split or missing ZIP directory.") }
        let count = Int(try u16(end + 10))
        let directoryBytes = Int(try u32(end + 12)), directoryStart = Int(try u32(end + 16))
        guard count > 0, count <= 20_000, directoryStart >= 0, directoryBytes > 0,
              directoryStart + directoryBytes == end else { throw invalid("unsupported ZIP directory or ZIP64 archive.") }
        var cursor = directoryStart
        var paths: [String] = []
        var seen = Set<String>()
        var expanded: Int64 = 0
        var ranges: [Range<Int>] = []
        for _ in 0..<count {
            guard cursor + 46 <= end, try u32(cursor) == 0x02014b50 else { throw invalid("missing file header.") }
            let flags = try u16(cursor + 8), method = try u16(cursor + 10)
            let compressed = Int(try u32(cursor + 20)), uncompressed = Int64(try u32(cursor + 24))
            let nameLength = Int(try u16(cursor + 28)), extraLength = Int(try u16(cursor + 30))
            let commentLength = Int(try u16(cursor + 32)), localOffset = Int(try u32(cursor + 42))
            let mode = (try u32(cursor + 38)) >> 16
            let kind = mode & 0xf000
            guard try u16(cursor + 34) == 0, flags & 1 == 0, [0, 8].contains(method),
                  [UInt32(0), 0x4000, 0x8000].contains(kind), mode & 0x0c00 == 0,
                  nameLength > 0, cursor + 46 + nameLength + extraLength + commentLength <= end else {
                throw invalid("encrypted, linked, privileged or unsupported file entry.")
            }
            let filename = data.subdata(in: (cursor + 46)..<(cursor + 46 + nameLength))
            guard let path = String(data: filename, encoding: .utf8), safeEntryPath(path),
                  seen.insert(path.lowercased()).inserted else {
                throw invalid("unexpected, duplicate or traversing path.")
            }
            guard localOffset >= 0, localOffset + 30 < directoryStart, try u32(localOffset) == 0x04034b50,
                  try u16(localOffset + 6) == flags, try u16(localOffset + 8) == method else {
                throw invalid("file and directory headers disagree.")
            }
            let localNameLength = Int(try u16(localOffset + 26)), localExtraLength = Int(try u16(localOffset + 28))
            let payload = localOffset + 30 + localNameLength + localExtraLength
            guard localNameLength == nameLength, payload <= directoryStart,
                  payload + compressed <= directoryStart,
                  data.subdata(in: (localOffset + 30)..<(localOffset + 30 + localNameLength)) == filename else {
                throw invalid("file paths or byte ranges disagree.")
            }
            expanded += uncompressed
            guard expanded <= MacReleasePolicy.maximumArchiveBytes else { throw invalid("expanded files exceed 512 MB.") }
            paths.append(path)
            ranges.append(localOffset..<(payload + compressed))
            cursor += 46 + nameLength + extraLength + commentLength
        }
        guard cursor == end,
              paths.contains("Evict.app/Contents/Info.plist"),
              paths.contains("Evict.app/Contents/MacOS/Evict") else {
            throw invalid("the native Evict application is missing.")
        }
        let ordered = ranges.sorted { $0.lowerBound < $1.lowerBound }
        for index in 1..<ordered.count where ordered[index - 1].upperBound > ordered[index].lowerBound {
            throw invalid("overlapping file entries.")
        }
        return Contents(paths: paths, expandedBytes: expanded)
    }

    private static func safeEntryPath(_ path: String) -> Bool {
        guard !path.isEmpty, !path.hasPrefix("/"), !path.contains("\\"), !path.contains(":"),
              !path.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else { return false }
        let components = path.split(separator: "/", omittingEmptySubsequences: false)
        guard !components.contains(".."), !components.contains("."),
              !components.dropLast().contains("") else { return false }
        return path == "Evict.app/" || path.hasPrefix("Evict.app/") ||
            path == "__MACOSX/" || path == "__MACOSX/._Evict.app" || path.hasPrefix("__MACOSX/Evict.app/")
    }
}
