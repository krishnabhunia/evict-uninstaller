import Foundation

public enum MacBrowser: String, CaseIterable, Codable, Sendable {
    case chrome = "Chrome", edge = "Edge", brave = "Brave", vivaldi = "Vivaldi", opera = "Opera", firefox = "Firefox"

    public var bundleIdentifier: String {
        switch self {
        case .chrome: return "com.google.Chrome"
        case .edge: return "com.microsoft.edgemac"
        case .brave: return "com.brave.Browser"
        case .vivaldi: return "com.vivaldi.Vivaldi"
        case .opera: return "com.operasoftware.Opera"
        case .firefox: return "org.mozilla.firefox"
        }
    }

    var supportPath: String {
        switch self {
        case .chrome: return "Google/Chrome"
        case .edge: return "Microsoft Edge"
        case .brave: return "BraveSoftware/Brave-Browser"
        case .vivaldi: return "Vivaldi"
        case .opera: return "com.operasoftware.Opera"
        case .firefox: return "Firefox/Profiles"
        }
    }

    public var managementURL: URL {
        let address: String
        switch self {
        case .chrome: address = "chrome://extensions/"
        case .edge: address = "edge://extensions/"
        case .brave: address = "brave://extensions/"
        case .vivaldi: address = "vivaldi://extensions/"
        case .opera: address = "opera://extensions/"
        case .firefox: address = "about:addons"
        }
        return URL(string: address)!
    }
}

public struct MacBrowserExtension: Identifiable, Sendable, Equatable {
    public var id: String { browser.rawValue + "/" + profile + "/" + identifier }
    public let browser: MacBrowser
    public let profile: String
    public let identifier: String
    public let name: String
    public let version: String
    public let permissions: [String]
    public let path: String
    public let isActive: Bool?

    public var hasBroadPermissions: Bool {
        permissions.contains { ["<all_urls>", "*://*/*", "http://*/*", "https://*/*", "debugger", "management", "clipboardRead"].contains($0) }
    }
}

public struct MacBrowserInventory: Sendable {
    public let extensions: [MacBrowserExtension]
    public let warnings: [String]
}

/// Read-only inventory. Enabling/removing is handed to the owning browser, which updates its
/// signed/protected configuration and synchronisation state correctly.
public enum MacBrowserExtensions {
    public static func scan() -> MacBrowserInventory {
        scan(applicationSupport: AppPaths.userLibrary + "/Application Support")
    }

    static func scan(applicationSupport: String) -> MacBrowserInventory {
        let fm = FileManager.default
        var found: [MacBrowserExtension] = []
        var warnings: [String] = []
        for browser in MacBrowser.allCases {
            let root = applicationSupport + "/" + browser.supportPath
            guard fm.fileExists(atPath: root) else { continue }
            guard MacMaintenancePaths.isUnaliased(root) else {
                warnings.append("Skipped linked \(browser.rawValue) profiles.")
                continue
            }
            guard let children = try? fm.contentsOfDirectory(atPath: root) else {
                warnings.append("Could not read \(browser.rawValue) profiles.")
                continue
            }
            if browser == .firefox {
                for profile in children.sorted() {
                    let path = root + "/" + profile + "/extensions.json"
                    guard MacMaintenancePaths.isUnaliased(path),
                          let data = boundedData(path), let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
                    found.append(contentsOf: firefox(json, profile: profile, path: path))
                }
            } else {
                var profiles = children.filter { $0 == "Default" || $0.hasPrefix("Profile ") }.map { (name: $0, path: root + "/" + $0) }
                if browser == .opera { profiles.append((name: "Default", path: root)) }
                for profile in profiles {
                    let extensionsRoot = profile.path + "/Extensions"
                    guard MacMaintenancePaths.isUnaliased(extensionsRoot), let identifiers = try? fm.contentsOfDirectory(atPath: extensionsRoot) else { continue }
                    for identifier in identifiers.sorted() where validChromiumIdentifier(identifier) {
                        let container = extensionsRoot + "/" + identifier
                        guard MacMaintenancePaths.isUnaliased(container), let versions = try? fm.contentsOfDirectory(atPath: container) else { continue }
                        let sorted = versions.sorted { $0.compare($1, options: .numeric) == .orderedDescending }
                        for version in sorted {
                            let folder = container + "/" + version
                            let manifest = folder + "/manifest.json"
                            guard MacMaintenancePaths.isUnaliased(manifest), let data = boundedData(manifest),
                                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
                            found.append(chromium(json, browser: browser, profile: profile.name, identifier: identifier, folder: folder))
                            break
                        }
                    }
                }
            }
        }
        var seen = Set<String>()
        return MacBrowserInventory(extensions: found.filter { seen.insert($0.id).inserted }.sorted {
            ($0.browser.rawValue, $0.profile, $0.name.lowercased()) < ($1.browser.rawValue, $1.profile, $1.name.lowercased())
        }, warnings: warnings)
    }

    static func validChromiumIdentifier(_ value: String) -> Bool {
        value.count == 32 && value.unicodeScalars.allSatisfy { (97...112).contains($0.value) }
    }

    private static func boundedData(_ path: String) -> Data? {
        guard FileSize.fileBytes(path) <= 8 * 1_024 * 1_024 else { return nil }
        return FileManager.default.contents(atPath: path)
    }

    static func chromium(_ json: [String: Any], browser: MacBrowser, profile: String, identifier: String, folder: String) -> MacBrowserExtension {
        var name = json["name"] as? String ?? identifier
        if name.hasPrefix("__MSG_"), name.hasSuffix("__"), let locale = json["default_locale"] as? String,
           !locale.isEmpty, locale.unicodeScalars.allSatisfy({ CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_-")).contains($0) }) {
            let key = String(name.dropFirst(6).dropLast(2))
            let messagesPath = folder + "/_locales/" + locale + "/messages.json"
            if MacMaintenancePaths.isUnaliased(messagesPath), let data = boundedData(messagesPath),
               let messages = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let entry = messages.first(where: { $0.key.lowercased() == key.lowercased() })?.value as? [String: Any],
               let translated = entry["message"] as? String { name = translated }
        }
        let permissions = (json["permissions"] as? [String] ?? []) + (json["host_permissions"] as? [String] ?? [])
        return MacBrowserExtension(browser: browser, profile: profile, identifier: identifier,
            name: name, version: json["version"] as? String ?? "Unknown", permissions: Array(Set(permissions)).sorted(), path: folder, isActive: nil)
    }

    static func firefox(_ json: [String: Any], profile: String, path: String) -> [MacBrowserExtension] {
        (json["addons"] as? [[String: Any]] ?? []).compactMap { addon in
            guard addon["type"] as? String == "extension", addon["isSystem"] as? Bool != true,
                  let identifier = addon["id"] as? String, !identifier.isEmpty else { return nil }
            let locale = addon["defaultLocale"] as? [String: Any]
            let grants = addon["userPermissions"] as? [String: Any] ?? [:]
            let permissions = (grants["permissions"] as? [String] ?? []) + (grants["origins"] as? [String] ?? [])
            return MacBrowserExtension(browser: .firefox, profile: profile, identifier: identifier,
                name: locale?["name"] as? String ?? identifier, version: addon["version"] as? String ?? "Unknown",
                permissions: Array(Set(permissions)).sorted(), path: path, isActive: addon["active"] as? Bool)
        }
    }
}
