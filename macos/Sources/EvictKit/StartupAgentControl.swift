import Foundation
#if canImport(Darwin)
import Darwin
#else
import Glibc
#endif

public enum StartupAgentError: LocalizedError, Sendable {
    case refused(String), query(String), operation(String)
    public var errorDescription: String? {
        switch self {
        case .refused(let text), .query(let text), .operation(let text): return text
        }
    }
}

public struct StartupAgentInspection: Sendable {
    public let path: String
    public let label: String
    public let isDisabled: Bool
    public let isLoaded: Bool
    public let canEnable: Bool
    let plistData: Data
    let defaultDisabled: Bool

    public var statusText: String {
        if isDisabled { return isLoaded ? "Disabled · still loaded" : "Disabled" }
        return isLoaded ? "Enabled" : "Enabled · not loaded"
    }
}

public struct StartupAgentInspections: Sendable {
    public let values: [String: StartupAgentInspection]
    public let errors: [String: String]
}

/// Reversible controls for jobs owned by the current user in their direct LaunchAgents folder.
/// No system jobs, Apple labels, arbitrary paths or Evict's own scheduled scan can reach a mutation.
public struct StartupAgentControl: Sendable {
    typealias Runner = @Sendable (String, [String]) -> Shell.Output
    private let root: String
    private let userID: UInt32
    private let domain: String
    private let run: Runner
    private let moveToTrash: @Sendable (String) throws -> Int64

    public init() {
        self.init(root: AppPaths.userLibrary + "/LaunchAgents", userID: getuid(),
                  runner: { Shell.run($0, $1, timeout: 10) },
                  moveToTrash: { try Remover().trash($0) })
    }

    /// Tests inject isolated roots, command results and a fixture-only Trash operation.
    init(root: String, userID: UInt32 = getuid(), runner: @escaping Runner,
         moveToTrash: @escaping @Sendable (String) throws -> Int64 = { try Remover().trash($0) }) {
        self.root = root
        self.userID = userID
        domain = "gui/\(userID)"
        run = runner
        self.moveToTrash = moveToTrash
    }

    private struct Descriptor {
        let item: StartupItem
        let data: Data
        let defaultDisabled: Bool
        let canEnable: Bool
    }

    private func validate(_ item: StartupItem, expected: StartupAgentInspection? = nil) throws -> Descriptor {
        let fm = FileManager.default
        guard !item.isSystemScope, !item.isDaemon,
              item.path.hasSuffix(".plist"),
              URL(fileURLWithPath: item.path).standardizedFileURL.path == item.path,
              MacMaintenancePaths.isDirectUnaliasedChild(item.path, root: root) else {
            throw StartupAgentError.refused("Only an unlinked plist directly inside your own LaunchAgents folder can be changed.")
        }
        guard Self.validLabel(item.label), !item.label.lowercased().hasPrefix("com.apple."), item.label.lowercased() != "com.apple" else {
            throw StartupAgentError.refused("Apple jobs and invalid service labels cannot be changed here.")
        }
        guard item.label.lowercased() != ScanScheduler.label.lowercased() else {
            throw StartupAgentError.refused("Manage Evict's scheduled scan in Settings.")
        }
        guard let attributes = try? fm.attributesOfItem(atPath: item.path),
              attributes[.type] as? FileAttributeType == .typeRegular,
              (attributes[.ownerAccountID] as? NSNumber)?.uint32Value == userID,
              ((attributes[.size] as? NSNumber)?.intValue ?? Int.max) <= 1_048_576,
              let data = fm.contents(atPath: item.path),
              let plist = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              plist["Label"] as? String == item.label,
              Plist.launchdProgram(plist) == item.program else {
            throw StartupAgentError.refused("The job's ownership or contents could not be verified. Rescan before changing it.")
        }
        if let expected {
            guard expected.path == item.path, expected.label == item.label, expected.plistData == data else {
                throw StartupAgentError.refused("The startup item changed after review. Rescan and review it again.")
            }
        }
        let program = Plist.launchdProgram(plist)
        let canEnable = program.map { $0.hasPrefix("/") && fm.isExecutableFile(atPath: $0) } ?? false
        return Descriptor(item: item, data: data, defaultDisabled: Plist.bool(plist, "Disabled"), canEnable: canEnable)
    }

    static func validLabel(_ label: String) -> Bool {
        guard (1...220).contains(label.count) else { return false }
        return label.unicodeScalars.allSatisfy { scalar in
            (65...90).contains(scalar.value) || (97...122).contains(scalar.value) ||
            (48...57).contains(scalar.value) || [45, 46, 95].contains(scalar.value)
        }
    }

    /// The textual launchctl format is parsed strictly. An unknown format never means "enabled".
    static func parseDisabledServices(_ text: String) throws -> [String: Bool] {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.hasPrefix("disabled services = {"), trimmed.hasSuffix("}"),
              let opening = trimmed.firstIndex(of: "{") else {
            throw StartupAgentError.query("macOS returned an unrecognized startup override list.")
        }
        let body = trimmed[trimmed.index(after: opening)..<trimmed.index(before: trimmed.endIndex)]
        let expression = try NSRegularExpression(pattern: #"^\s*"([^"\r\n]+)"\s*=>\s*(true|false)\s*[,;]?\s*$"#)
        var result: [String: Bool] = [:]
        for raw in body.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.isEmpty { continue }
            let ns = line as NSString
            guard let match = expression.firstMatch(in: line, range: NSRange(location: 0, length: ns.length)),
                  match.range.length == ns.length else {
                throw StartupAgentError.query("macOS returned an unreadable startup override. No job was changed.")
            }
            let label = ns.substring(with: match.range(at: 1))
            guard result[label] == nil else { throw StartupAgentError.query("macOS returned duplicate startup overrides.") }
            result[label] = ns.substring(with: match.range(at: 2)) == "true"
        }
        return result
    }

    private func disabledServices() throws -> [String: Bool] {
        let output = run("/bin/launchctl", ["print-disabled", domain])
        guard output.ok else { throw StartupAgentError.query("Could not read startup status: " + Self.detail(output)) }
        return try Self.parseDisabledServices(output.stdout)
    }

    private func loaded(_ descriptor: Descriptor) throws -> Bool {
        let target = domain + "/" + descriptor.item.label
        let output = run("/bin/launchctl", ["print", target])
        if !output.ok {
            let text = (output.stdout + output.stderr).lowercased()
            if [Int32(3), Int32(113)].contains(output.status),
               text.contains("could not find service") || text.contains("service not found") { return false }
            throw StartupAgentError.query("Could not verify \(descriptor.item.label): " + Self.detail(output))
        }
        let lines = output.stdout.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }
        guard lines.first == target + " = {",
              let pathLine = lines.first(where: { $0.hasPrefix("path = ") }) else {
            throw StartupAgentError.refused("The loaded job's identity could not be verified. It was not changed.")
        }
        var path = String(pathLine.dropFirst("path = ".count))
        if path.hasPrefix("\""), path.hasSuffix("\"") { path = String(path.dropFirst().dropLast()) }
        guard path == descriptor.item.path else {
            throw StartupAgentError.refused("A different job uses this label. It was not changed.")
        }
        return true
    }

    private func inspection(_ descriptor: Descriptor, overrides: [String: Bool]) throws -> StartupAgentInspection {
        StartupAgentInspection(path: descriptor.item.path, label: descriptor.item.label,
            isDisabled: overrides[descriptor.item.label] ?? descriptor.defaultDisabled,
            isLoaded: try loaded(descriptor), canEnable: descriptor.canEnable,
            plistData: descriptor.data, defaultDisabled: descriptor.defaultDisabled)
    }

    public func inspect(_ item: StartupItem) throws -> StartupAgentInspection {
        try inspection(validate(item), overrides: disabledServices())
    }

    public func inspect(_ items: [StartupItem]) -> StartupAgentInspections {
        var values: [String: StartupAgentInspection] = [:]
        var errors: [String: String] = [:]
        var descriptors: [Descriptor] = []
        for item in items {
            do { descriptors.append(try validate(item)) }
            catch { errors[item.id] = error.localizedDescription }
        }
        guard !descriptors.isEmpty else { return StartupAgentInspections(values: values, errors: errors) }
        do {
            let overrides = try disabledServices()
            for descriptor in descriptors {
                do { values[descriptor.item.id] = try inspection(descriptor, overrides: overrides) }
                catch { errors[descriptor.item.id] = error.localizedDescription }
            }
        } catch {
            for descriptor in descriptors { errors[descriptor.item.id] = error.localizedDescription }
        }
        return StartupAgentInspections(values: values, errors: errors)
    }

    private func command(_ arguments: [String]) throws {
        let output = run("/bin/launchctl", arguments)
        guard output.ok else { throw StartupAgentError.operation("macOS could not complete \(arguments[0]): " + Self.detail(output)) }
    }

    /// Enabling bootstraps the same reviewed job; disabling prevents future loads and stops its
    /// existing registration. A failed transaction restores and verifies the previous effective state.
    @discardableResult
    public func setEnabled(_ enabled: Bool, item: StartupItem, expected: StartupAgentInspection) throws -> StartupAgentInspection {
        let descriptor = try validate(item, expected: expected)
        let before = try inspection(descriptor, overrides: disabledServices())
        if enabled && !descriptor.canEnable { throw StartupAgentError.refused("The program is missing or cannot be executed. This job cannot be enabled.") }
        let target = domain + "/" + item.label
        var attemptedChange = false
        do {
            _ = try validate(item, expected: before)
            attemptedChange = true
            try command([enabled ? "enable" : "disable", target])
            if enabled {
                if try !loaded(descriptor) { try command(["bootstrap", domain, item.path]) }
            } else if try loaded(descriptor) {
                try command(["bootout", target])
            }
            _ = try validate(item, expected: before)
            let after = try inspect(item)
            guard after.isDisabled == !enabled, after.isLoaded == enabled else {
                throw StartupAgentError.operation("macOS did not confirm the requested startup state.")
            }
            return after
        } catch {
            guard attemptedChange else { throw error }
            let detail = restore(item, before: before)
            throw StartupAgentError.operation(error.localizedDescription + " " + detail)
        }
    }

    /// Stop and verify first, then invoke the same protected Trash operation used by uninstall.
    /// The prior registration is restored if moving the plist fails.
    @discardableResult
    public func trash(_ item: StartupItem, expected: StartupAgentInspection) throws -> Int64 {
        let descriptor = try validate(item, expected: expected)
        let before = try inspection(descriptor, overrides: disabledServices())
        var attemptedStop = false
        do {
            if before.isLoaded {
                attemptedStop = true
                try command(["bootout", domain + "/" + item.label])
            }
            guard try !loaded(descriptor) else {
                throw StartupAgentError.operation("The startup job is still loaded. Its plist was not moved.")
            }
            _ = try validate(item, expected: before)
            return try moveToTrash(item.path)
        } catch {
            let detail = attemptedStop ? restore(item, before: before) : "The startup plist was not moved."
            throw StartupAgentError.operation(error.localizedDescription + " " + detail)
        }
    }

    private func restore(_ item: StartupItem, before: StartupAgentInspection) -> String {
        do {
            let descriptor = try validate(item, expected: before)
            let isLoaded = try loaded(descriptor)
            let target = domain + "/" + item.label
            if before.isLoaded {
                if !isLoaded {
                    try command(["enable", target])
                    try command(["bootstrap", domain, item.path])
                }
            } else if isLoaded { try command(["bootout", target]) }
            try command([before.isDisabled ? "disable" : "enable", target])
            let restored = try inspect(item)
            guard restored.isDisabled == before.isDisabled, restored.isLoaded == before.isLoaded else {
                throw StartupAgentError.operation("The previous state could not be verified.")
            }
            return "The previous startup state was restored."
        } catch {
            return "The previous startup state could not be restored: \(error.localizedDescription)"
        }
    }

    private static func detail(_ output: Shell.Output) -> String {
        let message = output.stderr.isEmpty ? output.stdout : output.stderr
        return message.isEmpty ? "launchctl exited \(output.status)" : String(message.trimmingCharacters(in: .whitespacesAndNewlines).prefix(1_500))
    }
}
