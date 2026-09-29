import Foundation

/// Minimal wrapper around `Process` for the few command-line tools Evict reads from
/// (`pkgutil`, `brew`, `launchctl`, `mdls`). Nothing here ever deletes anything.
public enum Shell {
    public struct Output: Sendable {
        public let status: Int32
        public let stdout: String
        public let stderr: String
        public var ok: Bool { status == 0 }
        public var lines: [String] {
            stdout.split(separator: "\n", omittingEmptySubsequences: true).map { String($0).trimmingCharacters(in: .whitespaces) }
        }
    }

    /// Runs `launchPath` with `arguments` and waits up to `timeout` seconds.
    @discardableResult
    public static func run(_ launchPath: String, _ arguments: [String], timeout: TimeInterval = 20) -> Output {
        guard FileManager.default.isExecutableFile(atPath: launchPath) else {
            return Output(status: 127, stdout: "", stderr: "\(launchPath) is not available")
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err

        // A small box keeps the pipe readers off captured local vars (strict-concurrency clean).
        final class Buffers: @unchecked Sendable {
            let lock = NSLock()
            var out = Data()
            var err = Data()
        }
        let buffers = Buffers()
        do { try process.run() } catch {
            return Output(status: 126, stdout: "", stderr: error.localizedDescription)
        }
        // Read both pipes on background queues so a large output cannot deadlock the child.
        let group = DispatchGroup()
        for (pipe, isOut) in [(out, true), (err, false)] {
            group.enter()
            DispatchQueue.global().async {
                let data = pipe.fileHandleForReading.readDataToEndOfFile()
                buffers.lock.lock()
                if isOut { buffers.out = data } else { buffers.err = data }
                buffers.lock.unlock()
                group.leave()
            }
        }
        let deadline = Date().addingTimeInterval(timeout)
        while process.isRunning && Date() < deadline { usleep(50_000) }
        if process.isRunning { process.terminate() }
        process.waitUntilExit()
        _ = group.wait(timeout: .now() + 5)
        buffers.lock.lock()
        let outText = String(data: buffers.out, encoding: .utf8) ?? ""
        let errText = String(data: buffers.err, encoding: .utf8) ?? ""
        buffers.lock.unlock()
        return Output(status: process.terminationStatus, stdout: outText, stderr: errText)
    }
}
