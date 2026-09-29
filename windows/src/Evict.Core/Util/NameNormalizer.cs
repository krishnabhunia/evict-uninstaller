using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>A normalised name the program might use for a folder or registry key, with the confidence an exact match deserves.</summary>
public sealed record CandidateKey(string Key, LeftoverConfidence Confidence);

/// <summary>
/// Turns product / publisher display names into tokens that can be compared against
/// folder names and registry key names. Pure logic – covered by unit tests.
/// </summary>
public static partial class NameNormalizer
{
    /// <summary>Words that are far too generic to identify a program on their own.</summary>
    public static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // grammar / legal
        "the", "and", "for", "with", "by", "of", "inc", "ltd", "llc", "gmbh", "co", "corp", "corporation", "company",
        "limited", "technologies", "technology", "systems", "system", "solutions", "international", "software",
        // packaging noise
        "version", "edition", "setup", "install", "installer", "update", "updates", "updater", "program", "programs",
        "application", "applications", "app", "apps", "tool", "tools", "toolkit", "toolbox", "utility", "utilities",
        "x64", "x86", "64-bit", "32-bit", "64bit", "32bit", "bit", "beta", "alpha", "release", "preview", "stable",
        "free", "pro", "premium", "lite", "trial", "community", "enterprise", "professional", "standard", "ultimate",
        "deluxe", "express", "portable", "classic", "plus", "basic", "home", "business", "personal", "family",
        "runtime", "redistributable", "redist", "package", "packages", "client", "server", "suite", "driver", "drivers",
        "framework", "library", "libraries", "sdk", "api", "core", "base", "component", "components", "module", "modules",
        "extension", "extensions", "plugin", "plugins", "addon", "addons", "pack", "language", "en", "en-us", "english",
        // generic product words
        "windows", "microsoft", "common", "shared", "files", "data", "user", "users", "local", "roaming", "temp",
        "cache", "logs", "log", "config", "settings", "desktop", "mobile", "web", "net", "online", "cloud",
        "games", "game", "gaming", "launcher", "player", "media", "video", "audio", "music", "photo", "photos",
        "picture", "pictures", "image", "images", "office", "work", "manager", "management", "center", "centre",
        "control", "panel", "service", "services", "assistant", "helper", "agent", "monitor", "security", "antivirus",
        "browser", "reader", "viewer", "editor", "creator", "maker", "converter", "downloader", "recorder", "burner",
        "backup", "recovery", "repair", "cleaner", "optimizer", "booster", "protection", "guard", "defender", "firewall",
        "network", "wireless", "bluetooth", "display", "graphics", "sound", "camera", "printer", "scanner", "keyboard",
        "mouse", "touchpad", "mail", "email", "chat", "meeting", "meetings", "notes", "calendar", "contacts", "explorer",
        "finder", "search", "store", "shop", "market", "hub", "portal", "dashboard", "console", "terminal", "shell",
        "command", "script", "code", "compiler", "debugger", "designer", "builder", "workspace", "project", "projects",
        "document", "documents", "spreadsheet", "presentation", "database", "host", "connect", "connector", "link", "sync",
        "dev", "developer", "development", "test", "testing", "demo", "sample", "samples", "example", "examples",
        "documentation", "docs", "help", "support", "engine", "platform", "new", "old", "latest", "current", "vr",
        "device", "devices", "hardware", "firmware", "bios", "chipset", "wifi", "lan", "usb", "hd", "uhd", "4k",
    };

    /// <summary>
    /// Folder / key names that must never be proposed for deletion by a *name* match, even if a program happens
    /// to be called that. Compared case-insensitively against the leaf name.
    /// </summary>
    public static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Microsoft", "Microsoft Shared", "Common Files", "Program Files", "Program Files (x86)",
        "ProgramData", "Users", "Public", "Default", "All Users", "AppData", "Local", "LocalLow", "Roaming",
        "Temp", "Packages", "Programs", "System", "System32", "SysWOW64", "WindowsApps", "WindowsPowerShell",
        "Internet Explorer", "Windows NT", "Windows Defender", "Windows Mail", "Windows Media Player",
        "Windows Photo Viewer", "Windows Portable Devices", "Windows Security", "Windows Sidebar",
        "Reference Assemblies", "MSBuild", "dotnet", "Intel", "NVIDIA Corporation", "NVIDIA", "AMD", "Realtek",
        "Google", "Mozilla", "Adobe", "Oracle", "Java", "Python", "nodejs", "Git", "Docker", "Classes",
        "Policies", "Clients", "RegisteredApplications", "Wow6432Node", "CurrentVersion", "Explorer",
        "Desktop", "Documents", "Downloads", "Pictures", "Music", "Videos", "OneDrive", "Start Menu",
        "Startup", "SendTo", "Recent", "Fonts", "Installer", "Setup", "Uninstall", "Software",
        "Steam", "steamapps", "Epic Games", "Ubisoft", "Common", "Data", "Config", "Settings", "Apple",
        "Microsoft Office", "Office", "JetBrains", "Autodesk", "Dell", "HP", "Lenovo", "ASUS", "Acer", "Samsung",
    };

    // Anything in parentheses / brackets is packaging noise ("(x64)", "(64-bit x64)", "(User)", "(remove only)")
    // and never part of the folder name a program actually uses.
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]")]
    private static partial Regex ParenNoiseRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])(?:v|ver\.?|version)?\s*\d+(?:\.\d+){1,3}[a-z0-9\-]*(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s\-_\.\+#]")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    /// <summary>
    /// Produces a compact comparison key: lower-case, no version numbers, no bracketed architecture
    /// noise, no separators. "Google Chrome (x64) 118.0.5993" → "googlechrome".
    /// </summary>
    public static string ToKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        s = ParenNoiseRegex().Replace(s, " ");
        s = VersionRegex().Replace(s, " ");
        s = s.Replace("™", "").Replace("®", "").Replace("©", "");
        s = PunctuationRegex().Replace(s, " ");
        s = RemoveDiacritics(s);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Splits a display name into meaningful words (stop words, versions and architecture noise removed).
    /// "Notepad++ (64-bit x64)" → ["notepad++"]; "Adobe Acrobat Reader DC" → ["adobe","acrobat","dc"].
    /// </summary>
    public static List<string> Tokens(string? name)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(name)) return result;
        var s = ParenNoiseRegex().Replace(name, " ");
        s = VersionRegex().Replace(s, " ");
        s = s.Replace("™", " ").Replace("®", " ").Replace("©", " ");
        s = PunctuationRegex().Replace(s, " ");
        s = MultiSpaceRegex().Replace(s, " ").Trim();
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim('-', '_', '.').ToLowerInvariant();
            if (t.Length < 2) continue;
            if (StopWords.Contains(t)) continue;
            if (t.All(char.IsDigit)) continue;          // bare numbers ("2024") are not identifying
            result.Add(t);
        }
        return result;
    }

    /// <summary>
    /// Candidate names a program's folder or registry key might use, with the confidence an exact match earns:
    /// the whole normalised name (High), the first two significant tokens (Medium), each individual
    /// significant token of ≥ 4 chars (Low). Ordered by specificity.
    /// </summary>
    public static List<CandidateKey> CandidateKeys(string? displayName)
    {
        var keys = new List<CandidateKey>();
        void Add(string key, LeftoverConfidence conf)
        {
            if (key.Length == 0 || StopWords.Contains(key)) return;
            if (keys.Any(k => k.Key == key)) return;
            keys.Add(new CandidateKey(key, conf));
        }

        var whole = ToKey(displayName);
        if (whole.Length >= 3) Add(whole, LeftoverConfidence.High);

        var tokens = Tokens(displayName);
        if (tokens.Count >= 2)
        {
            var two = ToKey(tokens[0] + tokens[1]);
            if (two.Length >= 4) Add(two, LeftoverConfidence.Medium);
        }
        foreach (var t in tokens)
        {
            var k = ToKey(t);
            if (k.Length >= 4) Add(k, LeftoverConfidence.Low);
        }
        return keys;
    }

    /// <summary>
    /// Decides whether a folder / key leaf name belongs to the program described by <paramref name="candidates"/>.
    /// Returns the confidence of the match, or null when it does not match.
    /// </summary>
    public static LeftoverConfidence? Match(string leafName, IReadOnlyList<CandidateKey> candidates, bool allowFuzzy = true)
    {
        if (string.IsNullOrWhiteSpace(leafName) || candidates.Count == 0) return null;
        if (ProtectedNames.Contains(leafName.Trim())) return null;
        var leafKey = ToKey(leafName);
        if (leafKey.Length < 3 || StopWords.Contains(leafKey)) return null;

        foreach (var c in candidates)
        {
            if (string.Equals(leafKey, c.Key, StringComparison.Ordinal)) return c.Confidence;
        }

        if (!allowFuzzy) return null;

        // Prefix relationships against the full key only: "vlcmediaplayer" ↔ "vlc" is *not* accepted
        // (too short), but "notepadplusplus" ↔ "notepadplus" is. Both directions, at least half the length.
        var full = candidates[0].Key;
        if (full.Length >= 6 && leafKey.Length >= 5)
        {
            if (full.StartsWith(leafKey, StringComparison.Ordinal) && leafKey.Length * 2 >= full.Length)
                return LeftoverConfidence.Low;
            if (leafKey.StartsWith(full, StringComparison.Ordinal) && full.Length * 2 >= leafKey.Length)
                return LeftoverConfidence.Low;
        }
        return null;
    }

    public static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "ltd", "limited", "llc", "gmbh", "ag", "sa", "bv", "nv", "plc", "pty", "srl", "sro", "kk",
        "co", "corp", "corporation", "company", "the", "group", "holdings", "technologies", "technology", "software",
        "systems", "solutions", "international", "and", "&", "of", "oy", "ab", "as", "aps", "spa", "sas", "sarl", "ltda",
    };

    /// <summary>Publisher → comparison key, dropping legal suffixes. "Google LLC" → "google", "Microsoft Corporation" → "microsoft".</summary>
    public static string PublisherKey(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "";
        var s = publisher.Replace(",", " ").Replace("™", " ").Replace("®", " ");
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !LegalSuffixes.Contains(p.Replace(".", "").Trim(',')))
            .ToList();
        var key = ToKey(string.Concat(parts));
        return key.Length >= 2 ? key : ToKey(publisher);
    }
}
