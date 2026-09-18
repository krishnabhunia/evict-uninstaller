import Foundation

/// Appends to `~/Library/Application Support/Evict/evict.log`. Kept deliberately small –
/// the log is for working out what happened on a machine Claude cannot see.
public enum Log {
    private static let queue = DispatchQueue(label: "app.evict.log")
    private static let stamp: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"; return f
    }()

    public static func info(_ message: String) { write("INFO", message) }
    public static func warn(_ message: String) { write("WARN", message) }
    public static func error(_ message: String, _ error: Error? = nil) {
        write("ERROR", error == nil ? message : "\(message): \(error!.localizedDescription)")
    }

    private static func write(_ level: String, _ message: String) {
        queue.async {
            let line = "\(stamp.string(from: Date())) [\(level)] \(message)\n"
            let path = AppPaths.logFile
            guard let data = line.data(using: .utf8) else { return }
            if let handle = FileHandle(forWritingAtPath: path) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: data)
            } else {
                try? data.write(to: URL(fileURLWithPath: path))
            }
        }
    }
}
