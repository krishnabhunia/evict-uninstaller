import Foundation

/// Homebrew casks install real `.app` bundles, so Evict lists them and – when the user removes one –
/// offers to run `brew uninstall --cask` instead of dragging the bundle to the Trash behind Homebrew's back.
public enum HomebrewService {
    public static var brewPath: String? {
        AppPaths.homebrewPrefixes.map { $0 + "/bin/brew" }.first { FileManager.default.isExecutableFile(atPath: $0) }
    }

    public static var isInstalled: Bool { brewPath != nil }

    /// Cask token → the app bundle names it installed (as far as the Caskroom shows).
    public static func installedCasks() -> [String] {
        guard let brew = brewPath else { return [] }
        let output = Shell.run(brew, ["list", "--cask", "-1"], timeout: 30)
        return output.ok ? output.lines : []
    }

    /// The cask token that installed a given app bundle, if any.
    public static func cask(forBundlePath bundlePath: String, casks: [String]) -> String? {
        let bundleName = (bundlePath as NSString).lastPathComponent.replacingOccurrences(of: ".app", with: "")
        let normalizedBundle = NameMatching.normalize(bundleName)
        return casks.first { token in
            let normalizedToken = NameMatching.normalize(token)
            return normalizedToken == normalizedBundle || normalizedBundle.hasPrefix(normalizedToken) && normalizedToken.count >= 4
        }
    }

    /// The command Evict would run – shown to the user before anything happens.
    public static func uninstallCommand(cask: String) -> String { "brew uninstall --cask \(cask)" }
}
