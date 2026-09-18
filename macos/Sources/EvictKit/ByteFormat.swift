import Foundation

public enum ByteFormat {
    private static let formatter: ByteCountFormatter = {
        let f = ByteCountFormatter()
        f.countStyle = .file
        f.allowedUnits = [.useKB, .useMB, .useGB]
        return f
    }()

    /// "512 KB", "1.2 GB" – the same wording the Finder uses.
    public static func string(_ bytes: Int64) -> String {
        bytes <= 0 ? "—" : formatter.string(fromByteCount: bytes)
    }
}
