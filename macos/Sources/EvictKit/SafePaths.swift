import Foundation

/// The single gate every deletion in Evict passes through.
///
/// Nothing is removed unless `SafePaths.check` returns `.allowed`. The rules are deliberately
/// blunt: an unknown path is refused rather than guessed at. Even an allowed path is only ever
/// moved to the Trash (see `Remover`), never unlinked, so every removal stays reversible.
public enum SafePaths {

    /// Why a path may not be touched.
    public enum Verdict: Equatable {
        case allowed
        case refused(String)

        public var isAllowed: Bool { self == .allowed }
        public var reason: String? { if case .refused(let r) = self { return r }; return nil }
    }

    /// Roots protected by System Integrity Protection or otherwise owned by macOS itself.
    public static let systemRoots: [String] = [
        "/System", "/bin", "/sbin", "/usr/bin", "/usr/sbin", "/usr/lib", "/usr/libexec", "/usr/share",
        "/private/var/db/dslocal", "/private/var/db/ConfigurationProfiles", "/cores", "/dev",
        "/Library/Apple", "/Applications/Utilities", "/Library/Security",
    ]

    /// Folders that hold the user's own documents. Evict never removes anything inside them.
    public static var userDataRoots: [String] {
        let home = NSHomeDirectory()
        return ["Documents", "Desktop", "Downloads", "Movies", "Music", "Pictures", "Public", "Library/Mobile Documents",
                "Library/CloudStorage", "Library/Keychains", "Library/Messages", "Library/Mail", "Library/Photos",
                "Library/Safari", "Library/Calendars", "Library/AddressBook"]
            .map { home + "/" + $0 }
    }

    /// Paths that must never be removed even though they sit inside an otherwise allowed root.
    public static var neverRemove: Set<String> {
        let home = NSHomeDirectory()
        var set: Set<String> = ["/", "/Applications", "/Library", "/Users", "/private", "/private/var", "/opt", "/usr",
                                "/usr/local", "/usr/local/bin", "/Volumes", "/tmp", "/private/tmp", "/private/var/folders"]
        set.insert(home)
        for sub in ["Library", "Library/Application Support", "Library/Caches", "Library/Preferences", "Library/Containers",
                    "Library/Group Containers", "Library/Logs", "Library/LaunchAgents", "Library/Saved Application State",
                    "Library/HTTPStorages", "Library/WebKit", "Library/Cookies", "Library/Application Scripts",
                    "Library/Internet Plug-Ins", "Library/PreferencePanes", "Library/Services", "Library/Autosave Information",
                    "Applications", "Desktop", "Documents", "Downloads"] {
            set.insert(home + "/" + sub)
        }
        for sub in ["Application Support", "Caches", "Preferences", "LaunchAgents", "LaunchDaemons", "PrivilegedHelperTools",
                    "Logs", "Internet Plug-Ins", "PreferencePanes", "Services", "Extensions", "Frameworks", "Fonts",
                    "Audio", "Audio/Plug-Ins", "Audio/Plug-Ins/Components", "Audio/Plug-Ins/VST", "Audio/Plug-Ins/VST3",
                    "QuickLook", "Spotlight", "ScriptingAdditions", "Widgets", "StartupItems"] {
            set.insert("/Library/" + sub)
        }
        return set
    }

    /// Roots Evict is allowed to remove *inside* (never the root itself).
    public static var removableRoots: [String] {
        let home = NSHomeDirectory()
        let roots = [
            "\(home)/Library/Application Support", "\(home)/Library/Caches", "\(home)/Library/Preferences",
            "\(home)/Library/Containers", "\(home)/Library/Group Containers", "\(home)/Library/Logs",
            "\(home)/Library/LaunchAgents", "\(home)/Library/Saved Application State", "\(home)/Library/HTTPStorages",
            "\(home)/Library/WebKit", "\(home)/Library/Cookies", "\(home)/Library/Application Scripts",
            "\(home)/Library/Internet Plug-Ins", "\(home)/Library/PreferencePanes", "\(home)/Library/Services",
            "\(home)/Library/Autosave Information", "\(home)/Applications",
            "/Library/Application Support", "/Library/Caches", "/Library/Preferences", "/Library/LaunchAgents",
            "/Library/LaunchDaemons", "/Library/PrivilegedHelperTools", "/Library/Logs", "/Library/Internet Plug-Ins",
            "/Library/PreferencePanes", "/Library/Services", "/Library/Audio/Plug-Ins", "/Library/QuickLook",
            "/Library/Spotlight", "/Library/ScriptingAdditions", "/Library/Widgets", "/Library/StartupItems",
            "/Applications", "/usr/local/bin", "/usr/local/lib", "/opt/homebrew/Caskroom",
        ]
        return roots
    }

    /// Normalises a path: resolves `~`, strips a trailing slash and collapses `//`.
    public static func normalize(_ path: String) -> String {
        var p = (path as NSString).expandingTildeInPath
        while p.contains("//") { p = p.replacingOccurrences(of: "//", with: "/") }
        if p.count > 1 && p.hasSuffix("/") { p.removeLast() }
        return p
    }

    /// True when `path` is `root` itself or sits inside it.
    public static func isInside(_ path: String, root: String) -> Bool {
        let p = normalize(path), r = normalize(root)
        return p == r || p.hasPrefix(r + "/")
    }

    /// The decision. `.allowed` means: inside a known removable root, at least one level deep,
    /// not a protected system path, not the user's own data, and not one of the roots themselves.
    public static func check(_ rawPath: String) -> Verdict {
        let path = normalize(rawPath)

        guard path.hasPrefix("/") else { return .refused("Not an absolute path") }
        guard !path.contains("/../") && !path.hasSuffix("/..") else { return .refused("Path contains \"..\"") }
        guard path != "/" else { return .refused("The startup disk itself") }

        if neverRemove.contains(path) { return .refused("A folder macOS and other apps share") }

        for root in systemRoots where isInside(path, root: root) {
            return .refused("Protected by macOS (\(root))")
        }
        for root in userDataRoots where isInside(path, root: root) {
            return .refused("Your own files (\(shortName(root)))")
        }

        guard let root = removableRoots.first(where: { isInside(path, root: $0) }) else {
            return .refused("Outside the folders Evict is allowed to clean")
        }
        // Must be at least one level below the root: ".../Application Support/Vendor", never the root.
        let remainder = path.dropFirst(normalize(root).count)
        guard remainder.hasPrefix("/"), remainder.dropFirst().count >= 2 else {
            return .refused("Too close to the top of \(shortName(root))")
        }
        return .allowed
    }

    public static func isAllowed(_ path: String) -> Bool { check(path).isAllowed }

    /// True when removing this path needs administrator rights (anything outside the user's home).
    public static func needsAdmin(_ path: String) -> Bool {
        !isInside(normalize(path), root: NSHomeDirectory())
    }

    private static func shortName(_ path: String) -> String { (path as NSString).lastPathComponent }
}
