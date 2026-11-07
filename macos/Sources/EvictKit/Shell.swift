import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

/// Minimal wrapper around `Process` for the few command-line tools Evict reads from
/// (`pkgutil`, `brew`, `launchctl`, `mdls`). Nothing here ever deletes anything.
public enum Shell {
    public struct Output: Sendable {
        public let status: Int32
        public let stdout: String
        public let stderr: String
        public init(status: Int32, stdout: String, stderr: String) {
            self.status = status
            self.stdout = stdout
            self.stderr = stderr
        }
        public var ok: Bool { status == 0 }
        public var lines: [String] {
            stdout.split(separator: "\n", omittingEmptySubsequences: true).map { String($0).trimmingCharacters(in: .whitespaces) }
        }
    }

    /// Runs `launchPath` with `arguments` and waits up to `timeout` seconds.
    @discardableResult
    public static func run(_ launchPath: String, _ arguments: [String], timeout: TimeInterval = 20,
                           environment: [String: String]? = nil) -> Output {
        guard FileManager.default.isExecutableFile(atPath: launchPath) else {
            return Output(status: 127, stdout: "", stderr: "\(launchPath) is not available")
        }
        let process = Process()
        process.executableURL = URL(fileURLWithPath: launchPath)
        process.arguments = arguments
        process.standardInput = FileHandle.nullDevice
        if let environment {
            process.environment = ProcessInfo.processInfo.environment.merging(environment) { _, value in value }
        }
        let out = Pipe(), err = Pipe()
        process.standardOutput = out
        process.standardError = err

        // A small box keeps the pipe readers off captured local vars (strict-concurrency clean).
        final class Buffers: @unchecked Sendable {
            let lock = NSLock()
            var out = Data()
            var err = Data()
            var truncated = false
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
                defer { group.leave() }
                while let data = try? pipe.fileHandleForReading.read(upToCount: 65_536), !data.isEmpty {
                    buffers.lock.lock()
                    let remaining = 16 * 1_024 * 1_024 - (isOut ? buffers.out.count : buffers.err.count)
                    if data.count > remaining { buffers.truncated = true }
                    if remaining > 0 {
                        if isOut { buffers.out.append(data.prefix(remaining)) }
                        else { buffers.err.append(data.prefix(remaining)) }
                    }
                    buffers.lock.unlock()
                }
            }
        }
        let deadline = Date().addingTimeInterval(max(0.05, timeout))
        while process.isRunning && Date() < deadline { usleep(50_000) }
        let timedOut = process.isRunning
        if timedOut {
            process.terminate()
            let grace = Date().addingTimeInterval(1)
            while process.isRunning && Date() < grace { usleep(20_000) }
            if process.isRunning { _ = kill(process.processIdentifier, SIGKILL) }
            let killDeadline = Date().addingTimeInterval(2)
            while process.isRunning && Date() < killDeadline { usleep(20_000) }
        }
        // A descendant can inherit the pipes. Never wait indefinitely for it or an installer.
        if group.wait(timeout: .now() + 2) == .timedOut {
            try? out.fileHandleForReading.close()
            try? err.fileHandleForReading.close()
        }
        buffers.lock.lock()
        let outText = String(data: buffers.out, encoding: .utf8) ?? ""
        var errText = String(data: buffers.err, encoding: .utf8) ?? ""
        let truncated = buffers.truncated
        buffers.lock.unlock()
        if timedOut { errText += "\nThe command exceeded its time limit and was stopped." }
        if truncated { errText += "\nCommand output exceeded the supported size." }
        let status: Int32 = timedOut ? 124 : (truncated ? 125 : (process.isRunning ? 124 : process.terminationStatus))
        return Output(status: status, stdout: outText, stderr: errText)
    }
}
