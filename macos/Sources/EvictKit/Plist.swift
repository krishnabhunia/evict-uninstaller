import Foundation

/// Reads property lists (binary or XML) without pulling in any extra dependency.
public enum Plist {
    public static func read(atPath path: String) -> [String: Any]? {
        guard let data = FileManager.default.contents(atPath: path) else { return nil }
        return (try? PropertyListSerialization.propertyList(from: data, format: nil)) as? [String: Any]
    }

    public static func string(_ dict: [String: Any], _ key: String) -> String? {
        if let s = dict[key] as? String, !s.isEmpty { return s }
        return nil
    }

    public static func bool(_ dict: [String: Any], _ key: String) -> Bool {
        (dict[key] as? Bool) ?? ((dict[key] as? NSNumber)?.boolValue ?? false)
    }

    /// The executable a launchd job runs: `Program`, or the first entry of `ProgramArguments`.
    public static func launchdProgram(_ dict: [String: Any]) -> String? {
        if let program = string(dict, "Program") { return program }
        if let args = dict["ProgramArguments"] as? [String], let first = args.first, !first.isEmpty { return first }
        return nil
    }
}
