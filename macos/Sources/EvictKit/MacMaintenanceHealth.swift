import Foundation

public enum MacSecurityValue: String, Sendable {
    case enabled = "Enabled", disabled = "Disabled", unknown = "Not available"
}

public struct MacSecuritySnapshot: Sendable {
    public let gatekeeper: MacSecurityValue
    public let fileVault: MacSecurityValue
    public let systemIntegrity: MacSecurityValue

    public init(gatekeeper: MacSecurityValue = .unknown, fileVault: MacSecurityValue = .unknown,
                systemIntegrity: MacSecurityValue = .unknown) {
        self.gatekeeper = gatekeeper
        self.fileVault = fileVault
        self.systemIntegrity = systemIntegrity
    }

    public static func read() -> MacSecuritySnapshot {
        #if os(macOS)
        let gate = Shell.run("/usr/sbin/spctl", ["--status"], timeout: 5)
        let vault = Shell.run("/usr/bin/fdesetup", ["status"], timeout: 5)
        let integrity = Shell.run("/usr/bin/csrutil", ["status"], timeout: 5)
        return MacSecuritySnapshot(
            gatekeeper: value(gate.stdout, enabled: "assessments enabled", disabled: "assessments disabled"),
            fileVault: value(vault.stdout, enabled: "filevault is on", disabled: "filevault is off"),
            systemIntegrity: value(integrity.stdout, enabled: "status: enabled", disabled: "status: disabled"))
        #else
        return MacSecuritySnapshot()
        #endif
    }

    static func value(_ output: String, enabled: String, disabled: String) -> MacSecurityValue {
        let normalized = output.lowercased()
        func containsPhrase(_ phrase: String) -> Bool {
            normalized.range(of: "\\b" + NSRegularExpression.escapedPattern(for: phrase) + "\\b", options: .regularExpression) != nil
        }
        if containsPhrase(disabled) { return .disabled }
        if containsPhrase(enabled) { return .enabled }
        return .unknown
    }
}
