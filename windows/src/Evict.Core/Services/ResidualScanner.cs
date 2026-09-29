using System.Diagnostics;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public sealed class ResidualScanOptions
{
    /// <summary>Re-scan for leftovers of programs Evict (or you) uninstalled earlier, using the history.</summary>
    public bool FromHistory { get; set; } = true;
    /// <summary>Broken Programs &amp; Features entries whose folders and uninstaller vanished.</summary>
    public bool BrokenEntries { get; set; } = true;
    /// <summary>Heuristic: folders under Program Files / ProgramData / AppData that no installed program references.</summary>
    public bool UnmatchedFolders { get; set; } = true;
    /// <summary>Unmatched folders modified within this many days are skipped (they are probably in use).</summary>
    public int MinAgeDays { get; set; } = 30;
    public bool ScanAllUserProfiles { get; set; } = true;
}

/// <summary>
/// Residual Cleaner: finds what previous uninstalls (by any uninstaller) left behind.
/// Three sources, from reliable to heuristic: uninstall history, broken registry entries,
/// and folders that no installed program can account for.
/// </summary>
public sealed class ResidualScanner
{
    private readonly LeftoverScanner _scanner = new();

    /// <summary>Folder names under the roots that belong to Windows or are shared infrastructure – never candidates.</summary>
    private static readonly HashSet<string> SystemFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Packages", "Temp", "Programs", "Publishers", "VirtualStore", "ElevatedDiagnostics", "PlaceholderTileLogoFolder",
        "Comms", "ConnectedDevicesPlatform", "D3DSCache", "CrashDumps", "PeerDistRepub", "Diagnostics", "Application Data", "History",
        "Temporary Internet Files", "IsolatedStorage", "SquirrelTemp", "Package Cache", "regid.1991-06.com.microsoft", "SoftwareDistribution",
        "USOPrivate", "USOShared", "ssh", "Start Menu", "Templates", "Desktop", "Documents", "Favorites", "Libraries", "OneDrive",
        "Common Files", "Windows", "WindowsApps", "Windows Defender", "Windows Defender Advanced Threat Protection", "Windows Mail",
        "Windows Media Player", "Windows Multimedia Platform", "Windows NT", "Windows Photo Viewer", "Windows Portable Devices",
        "Windows Security", "Windows Sidebar", "WindowsPowerShell", "Internet Explorer", "MSBuild", "Reference Assemblies", "dotnet",
        "ModifiableWindowsApps", "Uninstall Information", "Intel", "NVIDIA", "NVIDIA Corporation", "AMD", "Realtek", "Dell", "HP", "Lenovo",
        "Synaptics", "Waves", "Conexant", "DTS", "Sonic Studio", "Nahimic", "Killer Networking", "Rivet Networks", "Oracle", "Java",
        "Python", "nodejs", "npm", "npm-cache", "pip", "Yarn", "NuGet", ".nuget", "JetBrains", "Docker", "Git", "GitHub", "Google", "Mozilla",
        "Adobe", "Apple", "Apple Computer", "Steam", "Epic Games", "Ubisoft", "EA", "Electronic Arts", "Riot Games", "Battle.net",
        "Roaming", "Local", "LocalLow", "AppData", "Users", "Public", "Default", "All Users", "Evict", "Package", "Setup", "Installer",
        "Common", "Shared", "Cache", "Logs", "Data", "Config", "Settings", "Backup", "Downloads", "ProgramData", "Program Files",
        "Program Files (x86)", "Autodesk", "Zoom", "Slack", "Discord", "Spotify", "Postman", "Notion", "Figma", "Microsoft Office", "Office",
    };

    public Task<LeftoverScanResult> ScanAsync(IReadOnlyList<InstalledProgram> installed, IReadOnlyList<UninstallHistoryEntry> history,
        ResidualScanOptions options, IProgress<ProgressReport>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(installed, history, options, progress, ct), ct);

    public LeftoverScanResult Scan(IReadOnlyList<InstalledProgram> installed, IReadOnlyList<UninstallHistoryEntry> history,
        ResidualScanOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new LeftoverScanResult();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installedKeys = BuildInstalledKeySet(installed);
        var scanOpts = new LeftoverScanOptions { ScanAllUserProfiles = options.ScanAllUserProfiles, IncludeLowConfidence = false };

        void Add(LeftoverItem item)
        {
            if (seen.Add(item.Kind + "|" + item.Path)) result.Items.Add(item);
        }

        // 1. History: programs removed earlier whose name no longer appears in the installed list.
        if (options.FromHistory)
        {
            var candidates = history
                .Where(h => h.Method is UninstallMethod.Standard or UninstallMethod.Quiet or UninstallMethod.Force or UninstallMethod.InstallLog)
                .Where(h => !(h.Notes ?? "").StartsWith("Windows app", StringComparison.OrdinalIgnoreCase) && !h.ProgramName.Contains(" extension)", StringComparison.OrdinalIgnoreCase))
                .Where(h => !installed.Any(p => NameNormalizer.ToKey(p.DisplayName) == NameNormalizer.ToKey(h.ProgramName)))
                .GroupBy(h => NameNormalizer.ToKey(h.ProgramName)).Select(g => g.OrderByDescending(h => h.Timestamp).First())
                .ToList();
            int i = 0;
            foreach (var h in candidates)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress?.Report(new ProgressReport($"Re-checking \"{h.ProgramName}\" ({i}/{candidates.Count})…", 5 + 35.0 * i / Math.Max(1, candidates.Count)));
                var fp = FingerprintFromHistory(h);
                if (fp.NameKeys.Count == 0) continue;
                var r = _scanner.Scan(fp, scanOpts, null, ct);
                foreach (var item in r.Items) Add(item);
            }
        }

        // 2. Broken entries: the registry entry itself plus whatever the name still matches on disk.
        if (options.BrokenEntries)
        {
            var broken = installed.Where(p => p.IsBrokenEntry).ToList();
            int i = 0;
            foreach (var p in broken)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                progress?.Report(new ProgressReport($"Broken entry: {p.DisplayName} ({i}/{broken.Count})…", 40 + 15.0 * i / Math.Max(1, broken.Count)));
                var sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + p.KeyName;
                Add(new LeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey, Path = RegistryPaths.Display(p.Hive, p.View, sub), Hive = p.Hive, RegView = p.View, SubKey = sub,
                    Confidence = IsSystemComponentLike(p) ? LeftoverConfidence.Low : LeftoverConfidence.High,
                    Detail = IsSystemComponentLike(p) ? "Entry whose uninstaller is gone – runtime/driver, review before removing" : "Programs & Features entry whose files are gone",
                    ProgramName = p.DisplayName,
                });
                var r = _scanner.Scan(LeftoverScanner.Fingerprint(p), scanOpts, null, ct);
                foreach (var item in r.Items) Add(item);
            }
        }

        // 3. Unmatched folders (heuristic, always "Review").
        if (options.UnmatchedFolders)
        {
            progress?.Report(new ProgressReport("Looking for folders no installed program accounts for…", 60));
            var running = RunningImageDirs();
            var cutoff = DateTime.Now.AddDays(-Math.Max(1, options.MinAgeDays));
            var roots = Roots(options.ScanAllUserProfiles).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int r = 0;
            foreach (var root in roots)
            {
                ct.ThrowIfCancellationRequested();
                r++;
                progress?.Report(new ProgressReport($"Scanning {root}…", 60 + 38.0 * r / Math.Max(1, roots.Count)));
                foreach (var item in ScanRootForUnmatched(root, installedKeys, installed, running, cutoff, ct)) Add(item);
            }
        }

        result.Elapsed = sw.Elapsed;
        var ordered = result.Items.OrderBy(i => i.Confidence).ThenBy(i => i.ProgramName).ThenBy(i => i.Kind).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        result.Items.Clear();
        result.Items.AddRange(ordered);
        progress?.Report(new ProgressReport($"Found {result.Items.Count} residual item(s).", 100));
        return result;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    internal static ProgramFingerprint FingerprintFromHistory(UninstallHistoryEntry h)
    {
        var keys = NameNormalizer.CandidateKeys(h.ProgramName).Where(k => k.Confidence != LeftoverConfidence.Low).ToList();
        var loc = h.InstallLocation;
        if (!string.IsNullOrEmpty(loc))
        {
            var leafKey = NameNormalizer.ToKey(PathUtil.LeafName(loc));
            if (leafKey.Length >= 3 && !NameNormalizer.StopWords.Contains(leafKey) && !keys.Any(k => k.Key == leafKey))
                keys.Add(new CandidateKey(leafKey, LeftoverConfidence.Medium));
        }
        return new ProgramFingerprint
        {
            DisplayName = h.ProgramName,
            Publisher = h.Publisher,
            InstallLocation = loc,
            NameKeys = keys,
            PublisherToken = NameNormalizer.PublisherKey(h.Publisher),
        };
    }

    /// <summary>Every normalised key that an installed program could use for a folder: names, tokens, publisher, install leaf, exe names.</summary>
    internal static HashSet<string> BuildInstalledKeySet(IEnumerable<InstalledProgram> installed)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in installed)
        {
            foreach (var k in NameNormalizer.CandidateKeys(p.DisplayName)) set.Add(k.Key);
            var pub = NameNormalizer.PublisherKey(p.Publisher);
            if (pub.Length >= 3) set.Add(pub);
            foreach (var tok in NameNormalizer.Tokens(p.Publisher)) if (tok.Length >= 3) set.Add(NameNormalizer.ToKey(tok));
            if (!string.IsNullOrEmpty(p.InstallLocation))
            {
                var leaf = NameNormalizer.ToKey(PathUtil.LeafName(p.InstallLocation));
                if (leaf.Length >= 2) set.Add(leaf);
                var parent = PathUtil.ParentPath(p.InstallLocation);
                if (parent != null) { var pl = NameNormalizer.ToKey(PathUtil.LeafName(parent)); if (pl.Length >= 3) set.Add(pl); }
            }
            if (!string.IsNullOrEmpty(p.PrimaryExecutable))
            {
                var exe = NameNormalizer.ToKey(PathUtil.LeafName(p.PrimaryExecutable).Replace(".exe", "", StringComparison.OrdinalIgnoreCase));
                if (exe.Length >= 3) set.Add(exe);
            }
        }
        return set;
    }

    private static HashSet<string> RunningImageDirs()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var path = ProcessUtil.GetImagePath(p.Id);
                    var dir = PathUtil.ParentPath(path);
                    if (dir != null) dirs.Add(PathUtil.NormalizeForCompare(dir));
                }
                catch { /* ignore */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* ignore */ }
        return dirs;
    }

    private static IEnumerable<string> Roots(bool allUsers)
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var mine = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var profiles = new List<string> { mine };
        if (allUsers && ElevationHelper.IsElevated)
        {
            try
            {
                var usersDir = PathUtil.ParentPath(mine);
                if (usersDir != null)
                    profiles.AddRange(Directory.EnumerateDirectories(usersDir).Where(d => Directory.Exists(Path.Combine(d, "AppData")) && !d.Equals(mine, StringComparison.OrdinalIgnoreCase)));
            }
            catch { /* ignore */ }
        }
        foreach (var profile in profiles)
        {
            yield return Path.Combine(profile, "AppData", "Local");
            yield return Path.Combine(profile, "AppData", "Local", "Programs");
            yield return Path.Combine(profile, "AppData", "Roaming");
            yield return Path.Combine(profile, "AppData", "LocalLow");
        }
    }

    private static IEnumerable<LeftoverItem> ScanRootForUnmatched(string root, HashSet<string> installedKeys, IReadOnlyList<InstalledProgram> installed,
        HashSet<string> runningDirs, DateTime cutoff, CancellationToken ct)
    {
        var results = new List<LeftoverItem>();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return results;
        IEnumerable<DirectoryInfo> dirs;
        try { dirs = new DirectoryInfo(root).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList(); }
        catch { return results; }

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var leaf = dir.Name;
            if (leaf.StartsWith('.') || leaf.StartsWith('{')) continue;                                  // dot-folders, GUID folders
            if (leaf.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || leaf.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)) continue;
            if (SystemFolderNames.Contains(leaf) || NameNormalizer.ProtectedNames.Contains(leaf)) continue;
            if ((dir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0) continue;
            var key = NameNormalizer.ToKey(leaf);
            if (key.Length < 3 || NameNormalizer.StopWords.Contains(key)) continue;

            // Does any installed program account for this folder?
            if (installedKeys.Contains(key)) continue;
            if (installed.Any(p => !string.IsNullOrEmpty(p.InstallLocation) && (PathUtil.IsUnder(p.InstallLocation, dir.FullName) || PathUtil.IsUnder(dir.FullName, p.InstallLocation)))) continue;
            if (installedKeys.Any(k => k.Length >= 5 && (key.StartsWith(k, StringComparison.Ordinal) || k.StartsWith(key, StringComparison.Ordinal)))) continue;

            // Something running from inside it? Then it is in use.
            if (runningDirs.Any(d => PathUtil.IsUnder(d, dir.FullName))) continue;

            // Recently touched → probably a portable app or live data.
            DateTime lastTouch;
            try { lastTouch = LastActivity(dir, ct); } catch { continue; }
            if (lastTouch > cutoff) continue;

            // Sub-folders named like an installed program (Publisher\Product)? Then the publisher folder is needed.
            bool childMatches = false;
            try
            {
                foreach (var sub in dir.EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true }).Take(60))
                {
                    if (installedKeys.Contains(NameNormalizer.ToKey(sub.Name))) { childMatches = true; break; }
                }
            }
            catch { /* ignore */ }
            if (childMatches) continue;

            if (PathUtil.IsProtectedRoot(dir.FullName, checkProtectedNames: true)) continue;

            long size = DirectorySizeCalculator.Measure(dir.FullName, ct, stopAfterBytes: 20L * SizeFormatter.GB) ?? 0;
            if (size == 0 && !dir.EnumerateFileSystemInfos().Any())
            {
                results.Add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = dir.FullName, SizeBytes = 0, Confidence = LeftoverConfidence.Medium, Detail = "Empty folder", ProgramName = "Unmatched folders" });
                continue;
            }
            results.Add(new LeftoverItem
            {
                Kind = LeftoverKind.Folder, Path = dir.FullName, SizeBytes = size, Confidence = LeftoverConfidence.Low,
                Detail = $"No installed program references this folder · last activity {lastTouch:dd MMM yyyy}",
                ProgramName = "Unmatched folders",
            });
        }
        return results;
    }

    /// <summary>Newest write time among the folder itself and its files two levels deep (capped for speed).</summary>
    private static DateTime LastActivity(DirectoryInfo dir, CancellationToken ct)
    {
        var newest = dir.LastWriteTime;
        int n = 0;
        foreach (var f in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = 2, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            ct.ThrowIfCancellationRequested();
            if (f.LastWriteTime > newest) newest = f.LastWriteTime;
            if (++n > 3000) break;
        }
        return newest;
    }

    /// <summary>Runtimes, redistributables, drivers and OEM services: other software depends on them, so never pre-select their entries.</summary>
    internal static bool IsSystemComponentLike(InstalledProgram p)
    {
        var n = (p.DisplayName ?? "") + " " + (p.Publisher ?? "");
        string[] hints = { "Redistributable", "Runtime", "Microsoft Corporation", "Driver", "Visual C++", ".NET", "SDK", "HP ", "Intel", "NVIDIA", "Realtek", "AMD", "Dell", "Lenovo" };
        return hints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

}
