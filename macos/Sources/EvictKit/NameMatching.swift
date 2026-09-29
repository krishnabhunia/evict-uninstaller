import Foundation

/// Decides whether a file name, folder name or bundle identifier belongs to a given application.
///
/// Every rule here is conservative on purpose: a wrong "yes" means deleting another app's data,
/// while a wrong "no" only means one leftover survives and can be removed by hand.
public enum NameMatching {

    /// Words that appear in so many names that they carry no evidence on their own.
    public static let weakTokens: Set<String> = [
        "app", "apps", "mac", "macos", "osx", "desktop", "pro", "plus", "lite", "free", "trial", "beta", "studio",
        "helper", "agent", "service", "services", "updater", "update", "launcher", "manager", "client", "player",
        "viewer", "editor", "tool", "tools", "suite", "edition", "software", "inc", "llc", "ltd", "gmbh", "corp",
        "company", "technologies", "technology", "labs", "team", "group", "the", "and", "for", "com", "org", "net",
    ]

    /// Lower-cases and strips everything that is not a letter or digit.
    public static func normalize(_ value: String) -> String {
        value.lowercased().unicodeScalars.reduce(into: "") { out, scalar in
            if CharacterSet.alphanumerics.contains(scalar) { out.unicodeScalars.append(scalar) }
        }
    }

    /// Splits a name into meaningful tokens: "Visual Studio Code" → ["visual", "studio", "code"],
    /// "TwoButtonApp" → ["two", "button", "app"].
    public static func tokens(_ value: String) -> [String] {
        var parts: [String] = []
        var current = ""
        var previousWasLower = false
        for character in value {
            if character.isLetter || character.isNumber {
                if character.isUppercase && previousWasLower && !current.isEmpty {
                    parts.append(current); current = ""
                }
                current.append(character)
                previousWasLower = character.isLowercase || character.isNumber
            } else {
                if !current.isEmpty { parts.append(current); current = "" }
                previousWasLower = false
            }
        }
        if !current.isEmpty { parts.append(current) }
        return parts.map { $0.lowercased() }.filter { !$0.isEmpty }
    }

    /// Tokens that actually identify the app (weak words and one/two-character fragments removed).
    public static func strongTokens(_ value: String) -> [String] {
        tokens(value).filter { $0.count >= 3 && !weakTokens.contains($0) }
    }

    /// True when `candidate` is the identifier itself or a sub-identifier of it:
    /// `com.vendor.app` matches `com.vendor.app.helper` but not `com.vendor.appstore`.
    public static func matchesBundleIdentifier(_ candidate: String, bundleIdentifier: String) -> Bool {
        let c = candidate.lowercased(), b = bundleIdentifier.lowercased()
        guard !b.isEmpty else { return false }
        if c == b { return true }
        if c.hasPrefix(b + ".") { return true }
        // "com.vendor.app.plist" / "com.vendor.app.savedState" – a file named after the identifier.
        if c.hasPrefix(b) {
            let rest = c.dropFirst(b.count)
            return rest.first == "." || rest.first == "-" || rest.first == "_"
        }
        return false
    }

    /// True when `candidate` starts with the vendor part of the identifier (`com.vendor.`).
    /// Weaker evidence than a full identifier match, so callers should treat it as Medium.
    public static func matchesVendorPrefix(_ candidate: String, bundleIdentifierPrefix: String) -> Bool {
        let c = candidate.lowercased(), p = bundleIdentifierPrefix.lowercased()
        guard p.split(separator: ".").count >= 2 else { return false }
        return c == p || c.hasPrefix(p + ".")
    }

    /// Name-based match, used only where an identifier is unavailable.
    ///
    /// Requires either the whole normalised app name, or every strong token of it, to be present.
    /// A single weak token ("Player", "Helper") never matches.
    public static func matchesAppName(_ candidate: String, appName: String) -> Bool {
        let haystack = normalize(candidate)
        let needle = normalize(appName)
        guard needle.count >= 3, !haystack.isEmpty else { return false }
        if haystack == needle { return true }

        let appTokens = strongTokens(appName)
        guard !appTokens.isEmpty else { return false }

        // Whole name present as a run of characters ("VisualStudioCode" inside "com.visualstudiocode.helper").
        if needle.count >= 5 && haystack.contains(needle) { return true }

        // Every strong token present, and at least two of them – one token alone is too weak.
        guard appTokens.count >= 2 else {
            return appTokens[0].count >= 5 && haystack.contains(appTokens[0])
        }
        return appTokens.allSatisfy { haystack.contains($0) }
    }

    /// The vendor name from an identifier: `com.company.app` → "company".
    public static func vendorToken(fromBundleIdentifier identifier: String) -> String? {
        let parts = identifier.lowercased().split(separator: ".")
        guard parts.count >= 2 else { return nil }
        let candidate = String(parts[1])
        return candidate.count >= 3 && !weakTokens.contains(candidate) ? candidate : nil
    }
}
