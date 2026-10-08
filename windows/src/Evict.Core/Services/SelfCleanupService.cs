using System.Text.RegularExpressions;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>Optional parts of Evict's own leftovers. Its Windows integration (registry, task, menus) is always removed.</summary>
[Flags]
public enum SelfCleanupParts
{
    None = 0,
    /// <summary>settings.json, diagnostic log, downloaded updates, scheduled-scan definition, bundleware list.</summary>
    Settings = 1,
    /// <summary>Uninstall history, Install Monitor logs and registry backups – the things that let you undo.</summary>
    History = 2,
    /// <summary>Orphaned Windows Installer packages System Cleanup moved to ProgramData\Evict\InstallerCacheBackup.</summary>
    InstallerCacheBackup = 4,
    /// <summary>"*.evict-backup" copies of browser preference files made when removing extensions.</summary>
    BrowserBackups = 8,
    /// <summary>What Windows itself recorded about Evict.exe: MuiCache, UserAssist, Compatibility Assistant, crash dumps, Prefetch.</summary>
    WindowsTraces = 16,
    All = Settings | History | InstallerCacheBackup | BrowserBackups | WindowsTraces,
}

/// <summary>One line of the "remove Evict's leftovers" dialog.</summary>
public sealed record SelfCleanupEntry(SelfCleanupParts Part, string Title, string Detail, long SizeBytes, int Count, bool NeedsAdmin, bool Recommended);

public sealed class SelfCleanupReport
{
    public List<string> Removed { get; } = new();
    public List<string> Failed { get; } = new();
}

/// <summary>
/// Removes what Evict itself left on the PC: run by the uninstaller (<c>Evict.exe --self-cleanup ask|&lt;parts&gt;</c>)
/// or, for the portable edition, from Settings. Never touches other programs' data.
/// </summary>
public static class SelfCleanupService
{
    public static string ScheduledTaskName => ScheduledScanTask.TaskNameForUser(ScheduledScanService.CurrentUserSid());
    private const string ContextMenuVerb = "EvictUninstall";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SendToShortcut = "Uninstall with Evict.lnk";

    /// <summary>Ticked by default: everything except the undo material (history, backups) and the installer-package backup.</summary>
    public const SelfCleanupParts DefaultParts = SelfCleanupParts.Settings | SelfCleanupParts.BrowserBackups | SelfCleanupParts.WindowsTraces;

    // ───────────────────────────── pure helpers (unit tested) ─────────────────────────────

    /// <summary>"settings,history" / "all" / "integration" (nothing optional) → flags. Unknown words are ignored.</summary>
    public static SelfCleanupParts ParseParts(string? text)
    {
        var parts = SelfCleanupParts.None;
        if (string.IsNullOrWhiteSpace(text)) return parts;
        foreach (var word in text.Split(new[] { ',', ';', '+', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            parts |= word.Trim().ToLowerInvariant() switch
            {
                "all" => SelfCleanupParts.All,
                "default" or "defaults" => DefaultParts,
                "settings" or "data" => SelfCleanupParts.Settings,
                "history" or "logs" or "backups" => SelfCleanupParts.History,
                "installercache" or "installer-cache" or "installercachebackup" => SelfCleanupParts.InstallerCacheBackup,
                "browser" or "browserbackups" => SelfCleanupParts.BrowserBackups,
                "traces" or "windowstraces" => SelfCleanupParts.WindowsTraces,
                _ => SelfCleanupParts.None, // "integration", "none" and anything unknown add nothing optional
            };
        }
        return parts;
    }

    public static string FormatParts(SelfCleanupParts parts)
    {
        if (parts == SelfCleanupParts.None) return "integration";
        var words = new List<string>();
        if (parts.HasFlag(SelfCleanupParts.Settings)) words.Add("settings");
        if (parts.HasFlag(SelfCleanupParts.History)) words.Add("history");
        if (parts.HasFlag(SelfCleanupParts.InstallerCacheBackup)) words.Add("installercache");
        if (parts.HasFlag(SelfCleanupParts.BrowserBackups)) words.Add("browser");
        if (parts.HasFlag(SelfCleanupParts.WindowsTraces)) words.Add("traces");
        return string.Join(",", words);
    }

    // These are the complete names emitted by packaging and the portable update handoff.
    // A prefix match would also claim unrelated applications and temporary files.
    private const string CoreNumber = @"(?:0|[1-9][0-9]*)";
    private const string PositiveNumber = @"[1-9][0-9]*";
    private const string PortableVersion = CoreNumber + @"\." + CoreNumber + @"\." + CoreNumber
        + @"(?:-beta\." + PositiveNumber + @"\." + PositiveNumber + @"\." + PositiveNumber + @")?";
    private const RegexOptions NameOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>The installed name, canonical versioned portable names, and exact update backup names.</summary>
    public static bool IsEvictExe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var leaf = PathUtil.LeafName(path.Trim().Trim('"'));
        return leaf.Equals("Evict.exe", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(leaf, @"\AEvict\.old(?:\." + PositiveNumber + @")?\.exe\z", NameOptions)
            || IsVersionedPortableStem(leaf.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? leaf[..^4] : "");
    }

    private static bool IsVersionedPortableStem(string name) =>
        Regex.IsMatch(name, @"\AEvict_" + PortableVersion + @"\z", NameOptions);

    internal static bool IsEvictCrashDump(string name)
    {
        var match = Regex.Match(PathUtil.LeafName(name), @"\A(?<exe>.+\.exe)\." + PositiveNumber + @"\.dmp\z", NameOptions);
        return match.Success && IsEvictExe(match.Groups["exe"].Value);
    }

    internal static bool IsEvictPrefetch(string name)
    {
        var match = Regex.Match(PathUtil.LeafName(name), @"\A(?<exe>.+\.exe)-[0-9a-f]{8}\.pf\z", NameOptions);
        return match.Success && IsEvictExe(match.Groups["exe"].Value);
    }

    internal static bool IsEvictNotificationKey(string name) =>
        name.Equals("Evict Uninstaller", StringComparison.OrdinalIgnoreCase) || IsEvictExe(name);

    internal static bool IsEvictExtractionName(string name) =>
        name.Equals("Evict", StringComparison.OrdinalIgnoreCase) || IsVersionedPortableStem(name);

    /// <summary>Select only immediate cache folders with an exact Evict host name; preserve links and other hosts.</summary>
    internal static IReadOnlyList<string> ExtractionDirectories(string baseDirectory)
    {
        var result = new List<string>();
        try
        {
            var parent = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(parent) || PathUtil.HasReparsePoint(parent)) return result;
            foreach (var directory in SafeDirectories(parent))
            {
                var full = Path.GetFullPath(directory);
                if (IsEvictExtractionName(Path.GetFileName(full))
                    && string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                    && !PathUtil.HasReparsePoint(full)) result.Add(full);
            }
        }
        catch { /* inaccessible or invalid cache bases are never removed */ }
        return result;
    }

    internal static void RemoveExtractionCaches(string baseDirectory, SelfCleanupReport report)
    {
        foreach (var root in ExtractionDirectories(baseDirectory))
        {
            foreach (var directory in SafeDirectories(root))
            {
                // Do not follow a link in the selected bundle cache.
                if (PathUtil.HasReparsePoint(directory)) continue;
                try { Directory.Delete(directory, recursive: true); report.Removed.Add(directory); } catch { /* current bundle is in use */ }
            }
            TryRemoveEmptyDirectory(root, report);
        }
    }

    private static readonly HashSet<string> HistoryEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "history.json", "history.json.bak", "install-logs", "registry-backups",
    };

    /// <summary>Which part a file or folder directly inside %LocalAppData%\Evict belongs to.</summary>
    public static SelfCleanupParts ClassifyDataEntry(string name) =>
        HistoryEntries.Contains(name) ? SelfCleanupParts.History : SelfCleanupParts.Settings;

    // ───────────────────────────── locations ─────────────────────────────

    public static string DataRoot => AppPaths.DataRootPath;

    /// <summary>
    /// Where .NET unpacks the native WPF libraries of the single-file Evict.exe (one sub-folder per version, ~25 MB
    /// each; IncludeNativeLibrariesForSelfExtract). The running version's folder is locked – see <see cref="ScheduleExtractionCleanup"/>.
    /// </summary>
    private static string ExtractionBaseDirectory => Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") is { Length: > 0 } b ? b : Path.Combine(Path.GetTempPath(), ".net");
    public static string ExtractionRoot => Path.Combine(ExtractionBaseDirectory, "Evict");
    public static string InstallerCacheBackupRoot => SystemCleanupService.BackupRoot;
    private static string ProgramDataEvict => Path.GetDirectoryName(InstallerCacheBackupRoot) ?? InstallerCacheBackupRoot;

    public static IEnumerable<string> BrowserBackupFiles()
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (var root in BrowserExtensionService.BackupSearchRoots())
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.evict-backup", opts).ToList(); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private static IEnumerable<string> CrashDumps()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
        return Directory.Exists(dir) ? SafeFiles(dir, "*.dmp").Where(IsEvictCrashDump) : Enumerable.Empty<string>();
    }

    private static IEnumerable<string> PrefetchFiles()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        return ElevationHelper.IsElevated && Directory.Exists(dir) ? SafeFiles(dir, "*.pf").Where(IsEvictPrefetch) : Enumerable.Empty<string>();
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); } catch { return Enumerable.Empty<string>(); }
    }

    // ───────────────────────────── description (for the dialog) ─────────────────────────────

    public static IReadOnlyList<SelfCleanupEntry> Describe()
    {
        var list = new List<SelfCleanupEntry>();
        long settingsBytes = 0, historyBytes = 0;
        int settingsCount = 0, historyCount = 0;
        if (Directory.Exists(DataRoot))
        {
            foreach (var entry in SafeEntries(DataRoot))
            {
                var size = SizeOf(entry);
                if (ClassifyDataEntry(Path.GetFileName(entry)) == SelfCleanupParts.History) { historyBytes += size; historyCount++; }
                else { settingsBytes += size; settingsCount++; }
            }
        }
        list.Add(new SelfCleanupEntry(SelfCleanupParts.Settings, "Settings, diagnostic log and downloaded updates",
            DataRoot, settingsBytes, settingsCount, NeedsAdmin: false, Recommended: true));
        list.Add(new SelfCleanupEntry(SelfCleanupParts.History, "Uninstall history, Install Monitor logs and registry backups",
            "Keep these if you may want to undo a registry cleanup or reuse an install log later. " + DataRoot, historyBytes, historyCount, NeedsAdmin: false, Recommended: false));

        long cacheBytes = Directory.Exists(InstallerCacheBackupRoot) ? SizeOf(InstallerCacheBackupRoot) : 0;
        list.Add(new SelfCleanupEntry(SelfCleanupParts.InstallerCacheBackup, "Backup of orphaned Windows Installer packages (System Cleanup)",
            "A program may still need one of these to repair or uninstall – remove only if every program works. " + InstallerCacheBackupRoot,
            cacheBytes, Directory.Exists(InstallerCacheBackupRoot) ? 1 : 0, NeedsAdmin: true, Recommended: false));

        var browser = BrowserBackupFiles().ToList();
        list.Add(new SelfCleanupEntry(SelfCleanupParts.BrowserBackups, "Browser-settings backups (*.evict-backup)",
            "Copies of browser preference files made when extensions were removed.", browser.Sum(SizeOf), browser.Count, NeedsAdmin: false, Recommended: true));

        int traces = CountWindowsTraces();
        list.Add(new SelfCleanupEntry(SelfCleanupParts.WindowsTraces, "Windows' records of Evict.exe",
            "Program-name cache, usage statistics, compatibility records, crash dumps and Prefetch files Windows keeps about Evict.", 0, traces, NeedsAdmin: false, Recommended: true));
        return list;
    }

    /// <summary>What is always removed, in words (shown in the dialog).</summary>
    public static IReadOnlyList<string> AlwaysRemoved => new[]
    {
        "Evict's registry keys (HKCU / HKLM\\Software\\Evict)",
        "\"Start with Windows\" entry and the scheduled Software Health scan",
        "\"Uninstall with Evict\" in Explorer's right-click and Send-to menus",
        "Evict.old.exe files left by portable updates",
        "Unpacked program libraries in Evict's installed and versioned portable .NET cache folders",
    };

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).ToList(); } catch { return Enumerable.Empty<string>(); }
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path)) return DirectorySizeCalculator.Measure(path) ?? 0;
        }
        catch { /* ignore */ }
        return 0;
    }

    // ───────────────────────────── run ─────────────────────────────

    /// <summary>Removes the integration (always) and the chosen optional parts. <paramref name="appDir"/> = Evict.exe's folder.</summary>
    public static SelfCleanupReport Run(SelfCleanupParts parts, string? appDir)
    {
        var r = new SelfCleanupReport();
        RemoveIntegration(r, appDir);

        if (parts.HasFlag(SelfCleanupParts.Settings) && parts.HasFlag(SelfCleanupParts.History))
        {
            DeleteDirectory(DataRoot, r);
        }
        else if (parts.HasFlag(SelfCleanupParts.Settings) || parts.HasFlag(SelfCleanupParts.History))
        {
            var wanted = parts.HasFlag(SelfCleanupParts.Settings) ? SelfCleanupParts.Settings : SelfCleanupParts.History;
            if (Directory.Exists(DataRoot))
                foreach (var entry in SafeEntries(DataRoot).Where(e => ClassifyDataEntry(Path.GetFileName(e)) == wanted))
                    DeleteEntry(entry, r);
        }

        if (parts.HasFlag(SelfCleanupParts.InstallerCacheBackup))
        {
            DeleteDirectory(InstallerCacheBackupRoot, r);
            TryRemoveEmptyDirectory(ProgramDataEvict, r);
        }

        if (parts.HasFlag(SelfCleanupParts.BrowserBackups))
            foreach (var f in BrowserBackupFiles().ToList()) DeleteEntry(f, r);

        if (parts.HasFlag(SelfCleanupParts.WindowsTraces))
            RemoveWindowsTraces(r);

        return r;
    }

    private static void RemoveIntegration(SelfCleanupReport r, string? appDir)
    {
        // Registry keys and values Setup or the app wrote.
        DeleteKey(RegistryHive.CurrentUser, RegistryView.Default, @"Software\Evict", r);
        DeleteValue(RegistryHive.CurrentUser, RegistryView.Default, RunKey, "Evict", r);
        foreach (var cls in new[] { "exefile", "lnkfile" })
            DeleteKey(RegistryHive.CurrentUser, RegistryView.Default, $@"Software\Classes\{cls}\shell\{ContextMenuVerb}", r);
        if (ElevationHelper.IsElevated)
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                DeleteKey(RegistryHive.LocalMachine, view, @"Software\Evict", r);
                DeleteValue(RegistryHive.LocalMachine, view, RunKey, "Evict", r);
            }
            foreach (var cls in new[] { "exefile", "lnkfile" })
                DeleteKey(RegistryHive.LocalMachine, RegistryView.Registry64, $@"Software\Classes\{cls}\shell\{ContextMenuVerb}", r);
        }

        // Scheduled scan (per user; schtasks needs no admin for the user's own task).
        try
        {
            var res = new ScheduledScanService().ApplyAsync("Off", 0, 0, CancellationToken.None).GetAwaiter().GetResult();
            if (res.Ok) r.Removed.Add("Scheduled scan for this user");
            else r.Failed.Add("Scheduled scan: " + res.Error);
        }
        catch (Exception ex) { r.Failed.Add($"Scheduled task: {ex.Message}"); }

        // Send-to shortcut (Setup removes the one it made; this also covers a copy made by hand).
        var sendTo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), SendToShortcut);
        if (File.Exists(sendTo)) DeleteEntry(sendTo, r);

        // Evict.old*.exe from portable self-updates.
        if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
            foreach (var f in SafeFiles(appDir, "Evict.old*.exe").Where(IsEvictExe)) DeleteEntry(f, r);

        // Unpacked native libraries of earlier versions (the running one is in use and fails silently).
        RemoveExtractionCaches(ExtractionBaseDirectory, r);
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); } catch { return Enumerable.Empty<string>(); }
    }

    /// <summary>
    /// Deletes the running version's unpacked libraries a few seconds after this process exits (they are locked
    /// until then) with a hidden, detached cmd.exe.
    /// </summary>
    public static void ScheduleExtractionCleanup()
    {
        try
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            foreach (var root in ExtractionDirectories(ExtractionBaseDirectory))
            {
                // cmd expands environment references even inside quotes; unusual custom paths fail closed.
                if (root.IndexOfAny(new[] { '"', '%', '!', '\r', '\n' }) >= 0) continue;
                var psi = new System.Diagnostics.ProcessStartInfo(cmd, $"/d /c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{root}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                System.Diagnostics.Process.Start(psi)?.Dispose();
            }
        }
        catch { /* best effort */ }
    }

    private static int CountWindowsTraces() => WindowsTraceValues().Count + WindowsTraceKeys().Count + CrashDumps().Count() + PrefetchFiles().Count();

    private const string MuiCache = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const string CompatStore = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";
    private const string UserAssist = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";
    private const string FeatureUsage = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage";
    private const string NotificationSettings = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    /// <summary>(subKey, valueName) pairs under HKCU that name Evict.exe.</summary>
    private static List<(string SubKey, string Value)> WindowsTraceValues()
    {
        var list = new List<(string, string)>();
        void Scan(string sub, Func<string, string?> pathOf)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(sub);
                if (k is null) return;
                foreach (var name in k.GetValueNames())
                    if (IsEvictExe(pathOf(name))) list.Add((sub, name));
            }
            catch { /* ignore */ }
        }
        Scan(MuiCache, RegistryCleanerRules.MuiCachePath);
        Scan(CompatStore, n => n);
        foreach (var sub in new[] { "AppSwitched", "ShowJumpView", "AppLaunch", "AppBadgeUpdated", "AppBadgeCleared", "TrayButtonClicked" })
            Scan($@"{FeatureUsage}\{sub}", n => n);
        try
        {
            using var ua = Registry.CurrentUser.OpenSubKey(UserAssist);
            foreach (var guid in ua?.GetSubKeyNames() ?? Array.Empty<string>())
                Scan($@"{UserAssist}\{guid}\Count", n => RegistryCleanerRules.Rot13(n));
        }
        catch { /* ignore */ }
        return list;
    }

    /// <summary>Notification-settings keys Windows created for Evict's tray notifications.</summary>
    private static List<string> WindowsTraceKeys()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(NotificationSettings);
            foreach (var name in k?.GetSubKeyNames() ?? Array.Empty<string>())
                if (IsEvictNotificationKey(name))
                    list.Add($@"{NotificationSettings}\{name}");
        }
        catch { /* ignore */ }
        return list;
    }

    private static void RemoveWindowsTraces(SelfCleanupReport r)
    {
        foreach (var (sub, value) in WindowsTraceValues()) DeleteValue(RegistryHive.CurrentUser, RegistryView.Default, sub, value, r);
        foreach (var sub in WindowsTraceKeys()) DeleteKey(RegistryHive.CurrentUser, RegistryView.Default, sub, r);
        foreach (var f in CrashDumps().Concat(PrefetchFiles()).ToList()) DeleteEntry(f, r);
    }

    // ───────────────────────────── primitives ─────────────────────────────

    private static void DeleteKey(RegistryHive hive, RegistryView view, string subKey, SelfCleanupReport r)
    {
        var display = RegistryPaths.Display(hive, view, subKey);
        try
        {
            var (parent, leaf) = RegistryPaths.Split(subKey);
            if (parent.Length == 0 || leaf.Length == 0) return;
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var parentKey = baseKey.OpenSubKey(parent, writable: true);
            if (parentKey is null || !parentKey.GetSubKeyNames().Contains(leaf, StringComparer.OrdinalIgnoreCase)) return;
            parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            r.Removed.Add(display);
        }
        catch (Exception ex) { r.Failed.Add($"{display}: {ex.Message}"); }
    }

    private static void DeleteValue(RegistryHive hive, RegistryView view, string subKey, string value, SelfCleanupReport r)
    {
        var display = RegistryPaths.Display(hive, view, subKey, value);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(subKey, writable: true);
            if (k is null || !k.GetValueNames().Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            k.DeleteValue(value, throwOnMissingValue: false);
            r.Removed.Add(display);
        }
        catch (Exception ex) { r.Failed.Add($"{display}: {ex.Message}"); }
    }

    private static void DeleteEntry(string path, SelfCleanupReport r)
    {
        if (Directory.Exists(path)) { DeleteDirectory(path, r); return; }
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
            r.Removed.Add(path);
        }
        catch (Exception ex) { r.Failed.Add($"{path}: {ex.Message}"); }
    }

    private static void DeleteDirectory(string path, SelfCleanupReport r)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(path, recursive: true);
            r.Removed.Add(path);
        }
        catch (Exception ex) { r.Failed.Add($"{path}: {ex.Message}"); }
    }

    private static void TryRemoveEmptyDirectory(string path, SelfCleanupReport r)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
                r.Removed.Add(path);
            }
        }
        catch { /* not empty or no rights – leave it */ }
    }
}
