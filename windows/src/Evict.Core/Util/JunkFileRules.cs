using System.Text.RegularExpressions;
using Evict.Core.Services;

namespace Evict.Core.Util;

/// <summary>
/// Pure rules for "Installation files": setup packages lying in Downloads / Desktop after the program was installed.
/// Unit tested.
/// </summary>
public static partial class InstallationFileRules
{
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle",
    };

    [GeneratedRegex(@"(?i)(setup|install|installer)")]
    private static partial Regex ArchiveHint();

    /// <summary>
    /// Why this file is a setup package ("Windows Installer package", "file name", …), or null when it is not one.
    /// Executables go through the same heuristics that detect installers as they start.
    /// </summary>
    public static string? InstallerReason(string path, string? fileDescription)
    {
        var leaf = PathUtil.LeafName(path);
        var ext = Path.GetExtension(leaf);
        if (PackageExtensions.Contains(ext)) return ext.Equals(".msi", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msp", StringComparison.OrdinalIgnoreCase) ? "Windows Installer package" : "app package";
        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) || ext.Equals(".7z", StringComparison.OrdinalIgnoreCase))
            return ArchiveHint().IsMatch(Path.GetFileNameWithoutExtension(leaf)) ? "setup archive" : null;
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        return InstallerHeuristics.Classify(path, fileDescription, null) is { } why ? "setup program (" + why + ")" : null;
    }

    /// <summary>Program name a setup file installs: "npp.8.6.Installer.x64.exe" → "npp"; description / product name win when present.</summary>
    public static string ProductGuess(string path, string? productName, string? fileDescription) =>
        InstallerHeuristics.FriendlyName(path, fileDescription, productName);

    /// <summary>The installed program's display name this setup file belongs to, or null.</summary>
    public static string? MatchInstalled(string productGuess, IEnumerable<string> installedDisplayNames)
    {
        var gk = NameNormalizer.ToKey(productGuess);
        if (gk.Length < 4) return null;
        string? best = null;
        int bestLen = 0;
        foreach (var name in installedDisplayNames)
        {
            var dk = NameNormalizer.ToKey(name);
            if (dk.Length < 4) continue;
            // "vlc" is too short to trust; "notepadplusplus" vs "notepadplusplus64bit" is a match either way round.
            bool match = gk == dk || (gk.StartsWith(dk, StringComparison.Ordinal) && dk.Length >= 5) || (dk.StartsWith(gk, StringComparison.Ordinal) && gk.Length >= 5);
            if (match && dk.Length > bestLen) { best = name; bestLen = dk.Length; }
        }
        return best;
    }
}

/// <summary>
/// Pure rules for "Software redundant files": caches, logs, crash reports and temp folders that installed programs keep
/// in their AppData / ProgramData folders. Unit tested.
/// </summary>
public static class RedundantFileRules
{
    private static readonly Dictionary<string, string> FolderKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cache"] = "cache", ["Caches"] = "cache", ["Code Cache"] = "cache", ["GPUCache"] = "cache", ["GrShaderCache"] = "cache",
        ["ShaderCache"] = "cache", ["DawnCache"] = "cache", ["DawnGraphiteCache"] = "cache", ["DawnWebGPUCache"] = "cache",
        ["CachedData"] = "cache", ["CachedExtensionVSIXs"] = "cache", ["Cache_Data"] = "cache", ["WebCache"] = "cache", ["cache2"] = "cache", ["startupCache"] = "cache",
        ["Logs"] = "logs", ["Log"] = "logs", ["LogFiles"] = "logs",
        ["CrashReports"] = "crash reports", ["Crash Reports"] = "crash reports", ["Crashes"] = "crash reports",
        ["CrashDumps"] = "crash reports", ["Dumps"] = "crash reports", ["reports"] = "crash reports",
        ["Temp"] = "temporary files", ["Tmp"] = "temporary files",
        ["Update Cache"] = "old updates", ["UpdateCache"] = "old updates", ["pending_updates"] = "old updates",
    };

    /// <summary>"reports" only counts inside a crash reporter's folder (Crashpad\reports, Crashpad\completed).</summary>
    private static readonly HashSet<string> CrashpadOnly = new(StringComparer.OrdinalIgnoreCase) { "reports", "completed" };

    /// <summary>Top-level folders never searched: Windows' own data, Store apps (System Cleanup handles them), Evict.</summary>
    private static readonly HashSet<string> SkippedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Evict", "CrashDumps", "ConnectedDevicesPlatform", "D3DSCache", "Comms",
        "PeerDistRepub", "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "Programs", "assembly",
    };

    /// <summary>"cache", "logs", "crash reports", "temporary files", "old updates" – or null for any other folder.</summary>
    public static string? KindOf(string folderName, string? parentName)
    {
        if (CrashpadOnly.Contains(folderName))
            return parentName != null && parentName.Contains("crashpad", StringComparison.OrdinalIgnoreCase) ? "crash reports" : null;
        return FolderKinds.TryGetValue(folderName, out var kind) ? kind : null;
    }

    /// <remarks>Deliberately not NameNormalizer.ProtectedNames: those guard whole-folder deletion, while only whitelisted
    /// inner folders (Cache, Logs…) are removed here – so Google\Chrome's or Mozilla\Firefox's caches are found.</remarks>
    public static bool IsSkippedRoot(string topFolderName) => SkippedRoots.Contains(topFolderName);

    /// <summary>
    /// The installed program that owns a folder, judged from its first two path segments below AppData / ProgramData
    /// ("Google\Chrome\User Data\…" → Google Chrome). Exact name-key matches win over partial ones, and the most
    /// specific key ("Google"+"Chrome", then "Chrome", then "Google") is tried first.
    /// </summary>
    public static string? OwnerOf(IReadOnlyList<string> segments, IReadOnlyList<(string DisplayName, string? Publisher)> installed)
    {
        if (segments.Count == 0) return null;
        var keys = new List<string>();
        if (segments.Count > 1)
        {
            keys.Add(NameNormalizer.ToKey(segments[0] + segments[1]));
            keys.Add(NameNormalizer.ToKey(segments[1]));
        }
        keys.Add(NameNormalizer.ToKey(segments[0]));
        keys.RemoveAll(k => k.Length < 3);
        var programs = installed.Select(p => (p.DisplayName, Key: NameNormalizer.ToKey(p.DisplayName))).Where(p => p.Key.Length >= 3).ToList();

        foreach (var k in keys)
            foreach (var p in programs)
                if (p.Key == k) return p.DisplayName;

        // "Code" (VS Code's folder) – the folder name is the start or end of the display name. When a key fits several
        // programs ("Google" → Chrome and Drive) the folder still belongs to installed software, labelled by its vendor folder.
        foreach (var k in keys.Where(k => k.Length >= 4))
        {
            var hits = programs.Where(p => p.Key.StartsWith(k, StringComparison.Ordinal) || p.Key.EndsWith(k, StringComparison.Ordinal)).ToList();
            if (hits.Count == 1) return hits[0].DisplayName;
            if (hits.Count > 1) return segments[0];
        }
        return null;
    }
}
