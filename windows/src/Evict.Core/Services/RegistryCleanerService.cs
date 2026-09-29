using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public enum RegistryIssueCategory
{
    BrokenUninstallEntries,
    AppPaths,
    StartupEntries,
    SharedDlls,
    MuiCache,
    OpenWithApplications,
    FileTypeCommands,
    ObsoleteSoftware,
    EmptyKeys,
    ComObjects,
    TypeLibraries,
    InstallerFolders,
    PrivacyTraces,
}

/// <summary>One category of the Registry Cleaner with the entries it found.</summary>
public sealed class RegistryIssueGroup
{
    public required RegistryIssueCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>Riskier checks: shown, but nothing in them is ticked until the user opts in.</summary>
    public bool Advanced { get; init; }
    /// <summary>Not about broken entries at all (history lists) – ticked only on request.</summary>
    public bool IsPrivacy { get; init; }
    public List<LeftoverItem> Items { get; } = new();
    public string? Error { get; set; }
    public bool TouchesLocalMachine => Items.Any(i => i.Hive == RegistryHive.LocalMachine);
}

/// <summary>
/// Scans the registry for entries that point at files and folders which no longer exist, plus (on request) COM,
/// Windows Installer and privacy traces. Findings are <see cref="LeftoverItem"/>s, so <see cref="LeftoverCleaner"/>
/// removes them – after exporting each one to a .reg backup.
/// </summary>
public sealed class RegistryCleanerService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string SharedDllsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
    private const string MuiCachePath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
    private const string InstallerFoldersPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders";
    private const string ExplorerPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private static readonly (RegistryHive Hive, RegistryView View)[] MachineViews =
        { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) };
    private static readonly (RegistryHive Hive, RegistryView View)[] AllViews =
        { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) };

    private readonly string? _windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private readonly Dictionary<string, bool> _drives = new(StringComparer.OrdinalIgnoreCase);

    public Task<List<RegistryIssueGroup>> ScanAsync(IProgress<ProgressReport>? progress, CancellationToken ct) =>
        Task.Run(() => Scan(progress, ct), ct);

    public List<RegistryIssueGroup> Scan(IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var steps = new (string Message, Func<RegistryIssueGroup> Run)[]
        {
            ("Checking uninstall entries…", ScanUninstallEntries),
            ("Checking App Paths…", ScanAppPaths),
            ("Checking startup entries…", ScanStartup),
            ("Checking shared DLL records…", ScanSharedDlls),
            ("Checking the program-name cache (MuiCache)…", ScanMuiCache),
            ("Checking \"Open with\" applications…", ScanOpenWith),
            ("Checking file-type commands…", ScanFileTypeCommands),
            ("Checking software keys of removed programs…", ScanObsoleteSoftware),
            ("Checking empty software keys…", ScanEmptyKeys),
            ("Checking COM / ActiveX registrations…", ScanComObjects),
            ("Checking type libraries…", ScanTypeLibraries),
            ("Checking Windows Installer folder records…", ScanInstallerFolders),
            ("Checking history lists…", ScanPrivacyTraces),
        };
        var groups = new List<RegistryIssueGroup>();
        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressReport(steps[i].Message, 100.0 * i / steps.Length));
            RegistryIssueGroup g;
            try { g = steps[i].Run(); }
            catch (Exception ex)
            {
                Log.Warn($"Registry cleaner step '{steps[i].Message}' failed: {ex.Message}");
                g = new RegistryIssueGroup { Category = (RegistryIssueCategory)i, Title = steps[i].Message.TrimEnd('…'), Description = "", Error = ex.Message };
            }
            groups.Add(g);
            Log.Info($"Registry cleaner: {g.Category} → {g.Items.Count} item(s)");
        }
        progress?.Report(new ProgressReport("Done.", 100));
        return groups;
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private bool Missing(string? path) => RegistryCleanerRules.IsDefinitelyMissing(path, Exists, DriveAvailable, _windowsDir);

    private static bool Exists(string p)
    {
        try { return File.Exists(p) || Directory.Exists(p); } catch { return true; }
    }

    /// <summary>Only fixed, ready drives count: a missing file on a USB stick or network share is not "missing".</summary>
    private bool DriveAvailable(string root)
    {
        if (_drives.TryGetValue(root, out var ok)) return ok;
        try
        {
            var d = new DriveInfo(root);
            ok = d.IsReady && d.DriveType == DriveType.Fixed;
        }
        catch { ok = false; }
        _drives[root] = ok;
        return ok;
    }

    private static LeftoverItem KeyItem(RegistryHive hive, RegistryView view, string subKey, string detail, bool safe, string group) => new()
    {
        Kind = LeftoverKind.RegistryKey,
        Path = RegistryPaths.Display(hive, view, subKey),
        Hive = hive, RegView = view, SubKey = subKey,
        Detail = detail,
        Confidence = safe ? LeftoverConfidence.High : LeftoverConfidence.Low,
        ProgramName = group,
    };

    private static LeftoverItem ValueItem(RegistryHive hive, RegistryView view, string subKey, string valueName, string detail, bool safe, string group) => new()
    {
        Kind = LeftoverKind.RegistryValue,
        Path = RegistryPaths.Display(hive, view, subKey, valueName),
        Hive = hive, RegView = view, SubKey = subKey, ValueName = valueName,
        Detail = detail,
        Confidence = safe ? LeftoverConfidence.High : LeftoverConfidence.Low,
        ProgramName = group,
    };

    private static RegistryKey? Open(RegistryHive hive, RegistryView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(subKey);
        }
        catch { return null; }
    }

    private static string? DefaultString(RegistryKey? key)
    {
        try { return key?.GetValue("") as string; } catch { return null; }
    }

    /// <summary>HKLM Registry32 and Registry64 are the same key for most of HKLM\SOFTWARE\Classes; report each key once.</summary>
    private static bool Seen(HashSet<string> seen, RegistryHive hive, string subKey, string? value = null) =>
        !seen.Add($"{hive}|{subKey}|{value}");

    // ───────────────────────────── safe checks ─────────────────────────────

    private RegistryIssueGroup ScanUninstallEntries()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.BrokenUninstallEntries,
            Title = "Broken uninstall entries",
            Description = "Programs & Features entries whose uninstaller and install folder are both gone – the program was deleted by hand. The same list as Programs → Broken Entries.",
        };
        var programs = new InstalledProgramsService().Enumerate(new ProgramsQueryOptions { MeasureMissingSizes = false, ReadUsageData = false });
        foreach (var p in programs.Where(p => p.IsBrokenEntry))
        {
            // Stricter than the Programs tab: a program on an unplugged drive is not broken.
            var cmd = UninstallCommandParser.Parse(p.UninstallString);
            bool uninstallerGone = cmd is null || (RegistryCleanerRules.IsAbsoluteLocal(cmd.FileName) && Missing(cmd.FileName));
            bool folderGone = string.IsNullOrWhiteSpace(p.InstallLocation) || Missing(p.InstallLocation);
            if (!uninstallerGone || !folderGone) continue;
            g.Items.Add(KeyItem(p.Hive, p.View, $@"{UninstallPath}\{p.KeyName}",
                $"\"{p.DisplayName}\" – uninstaller {(cmd is null ? "not set" : "missing: " + cmd.FileName)}", safe: true, g.Title));
        }
        return g;
    }

    private RegistryIssueGroup ScanAppPaths()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.AppPaths,
            Title = "App Paths to missing programs",
            Description = "Entries that let you start a program by name (Win+R → \"app.exe\") but point to a file that no longer exists.",
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view) in AllViews)
        {
            using var root = Open(hive, view, AppPathsPath);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                var target = DefaultString(k)?.Trim().Trim('"');
                if (target is null || !Missing(target)) continue;
                var sub = $@"{AppPathsPath}\{name}";
                if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, sub))) continue;
                g.Items.Add(KeyItem(hive, view, sub, "Missing: " + target, safe: true, g.Title));
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanStartup()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.StartupEntries,
            Title = "Startup entries to missing programs",
            Description = "Run / RunOnce entries that try to start a program at sign-in which no longer exists.",
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view) in AllViews)
        {
            foreach (var runKey in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                using var k = Open(hive, view, runKey);
                if (k is null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (name.Length == 0) continue;
                    var target = RegistryCleanerRules.MainTarget(k.GetValue(name) as string);
                    if (target is null || !Missing(target)) continue;
                    if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, runKey), name)) continue;
                    var item = ValueItem(hive, view, runKey, name, "Missing: " + target, safe: true, g.Title);
                    g.Items.Add(item);
                }
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanSharedDlls()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.SharedDlls,
            Title = "Shared DLL records of missing files",
            Description = "Reference counts that installers keep for shared files. Records of files that are gone are useless.",
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view) in MachineViews)
        {
            using var k = Open(hive, view, SharedDllsPath);
            if (k is null) continue;
            foreach (var name in k.GetValueNames())
            {
                if (!Missing(name)) continue;
                if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, SharedDllsPath), name)) continue;
                g.Items.Add(ValueItem(hive, view, SharedDllsPath, name, "Missing file", safe: true, g.Title));
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanMuiCache()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.MuiCache,
            Title = "Program-name cache (MuiCache)",
            Description = "Windows remembers the display name of every program you ran. Entries of programs that no longer exist.",
        };
        using var k = Open(RegistryHive.CurrentUser, RegistryView.Default, MuiCachePath);
        if (k is null) return g;
        foreach (var name in k.GetValueNames())
        {
            var path = RegistryCleanerRules.MuiCachePath(name);
            if (path is null || !Missing(path)) continue;
            g.Items.Add(ValueItem(RegistryHive.CurrentUser, RegistryView.Default, MuiCachePath, name, "Missing: " + path, safe: true, g.Title));
        }
        return g;
    }

    /// <summary>Targets of every shell\*\command under a key; empty when it has no commands.</summary>
    private static List<string?> VerbTargets(RegistryKey key)
    {
        var list = new List<string?>();
        using var shell = key.OpenSubKey("shell");
        if (shell is null) return list;
        foreach (var verb in shell.GetSubKeyNames())
        {
            using var cmd = shell.OpenSubKey(verb + @"\command");
            if (cmd is null) continue;
            list.Add(RegistryCleanerRules.MainTarget(DefaultString(cmd)));
        }
        return list;
    }

    private RegistryIssueGroup ScanOpenWith()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.OpenWithApplications,
            Title = "\"Open with\" entries of missing programs",
            Description = "Programs listed in the \"Open with\" menu whose program file is gone.",
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64) })
        {
            var rootPath = @"Software\Classes\Applications";
            using var root = Open(hive, view, rootPath);
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var app = root.OpenSubKey(name);
                if (app is null) continue;
                var targets = VerbTargets(app);
                // Every command must name a file, and every one of those files must be gone.
                if (targets.Count == 0 || targets.Any(t => t is null || !Missing(t))) continue;
                var sub = $@"{rootPath}\{name}";
                if (Seen(seen, hive, sub)) continue;
                g.Items.Add(KeyItem(hive, view, sub, "Missing: " + targets[0], safe: true, g.Title));
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanFileTypeCommands()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.FileTypeCommands,
            Title = "File-type commands of missing programs",
            Description = "Your file types (ProgIDs) with Open / Edit commands that start a program which no longer exists. Only the broken command is removed, not the file type. Review before removing.",
        };
        const string classes = @"Software\Classes";
        using var root = Open(RegistryHive.CurrentUser, RegistryView.Default, classes);
        if (root is null) return g;
        foreach (var progId in root.GetSubKeyNames())
        {
            if (progId.StartsWith('.') || progId.StartsWith('*') || progId.Equals("CLSID", StringComparison.OrdinalIgnoreCase)
                || progId.Equals("Applications", StringComparison.OrdinalIgnoreCase) || progId.Equals("Local Settings", StringComparison.OrdinalIgnoreCase)
                || progId.Equals("TypeLib", StringComparison.OrdinalIgnoreCase) || progId.Equals("Interface", StringComparison.OrdinalIgnoreCase)
                || progId.Equals("Directory", StringComparison.OrdinalIgnoreCase) || progId.Equals("Folder", StringComparison.OrdinalIgnoreCase)
                || progId.Equals("Drive", StringComparison.OrdinalIgnoreCase) || progId.Equals("AllFilesystemObjects", StringComparison.OrdinalIgnoreCase))
                continue;
            using var pk = root.OpenSubKey(progId + @"\shell");
            if (pk is null) continue;
            foreach (var verb in pk.GetSubKeyNames())
            {
                using var cmd = pk.OpenSubKey(verb + @"\command");
                var target = RegistryCleanerRules.MainTarget(DefaultString(cmd));
                if (target is null || !Missing(target)) continue;
                g.Items.Add(KeyItem(RegistryHive.CurrentUser, RegistryView.Default, $@"{classes}\{progId}\shell\{verb}",
                    $"{progId} → \"{verb}\" starts missing {target}", safe: false, g.Title));
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanObsoleteSoftware()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.ObsoleteSoftware,
            Title = "Settings keys of removed programs",
            Description = "SOFTWARE\\<Vendor>\\<Program> keys whose recorded install folder no longer exists. Some programs keep settings here on purpose – review before removing.",
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view) in AllViews)
        {
            using var software = Open(hive, view, "SOFTWARE");
            if (software is null) continue;
            foreach (var vendor in software.GetSubKeyNames())
            {
                if (RegistryCleanerRules.IsProtectedSoftwareKey(vendor)) continue;
                using var vk = software.OpenSubKey(vendor);
                if (vk is null) continue;
                var vendorPath = RegistryCleanerRules.InstallPathOf(ReadStrings(vk));
                // A vendor key is only a candidate on its own when it holds no product keys that may still be in use.
                if (vendorPath != null && vk.SubKeyCount == 0 && Missing(vendorPath))
                {
                    if (!Seen(seen, hive, RegFileFormat.KeyPath(hive, view, "SOFTWARE\\" + vendor)))
                        g.Items.Add(KeyItem(hive, view, $@"SOFTWARE\{vendor}", "Install folder missing: " + vendorPath, safe: false, g.Title));
                    continue;
                }
                foreach (var product in SafeSubKeyNames(vk))
                {
                    if (RegistryCleanerRules.IsProtectedSoftwareKey(product)) continue;
                    using var pk = vk.OpenSubKey(product);
                    if (pk is null) continue;
                    var path = RegistryCleanerRules.InstallPathOf(ReadStrings(pk));
                    if (path is null || !Missing(path)) continue;
                    var sub = $@"SOFTWARE\{vendor}\{product}";
                    if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, sub))) continue;
                    g.Items.Add(KeyItem(hive, view, sub, "Install folder missing: " + path, safe: false, g.Title));
                }
            }
        }
        return g;
    }

    private static IReadOnlyDictionary<string, string?> ReadStrings(RegistryKey key)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in key.GetValueNames())
            {
                try { if (key.GetValue(n) is string s) d[n] = s; } catch { /* unreadable */ }
            }
        }
        catch { /* ignore */ }
        return d;
    }

    private static string[] SafeSubKeyNames(RegistryKey k)
    {
        try { return k.GetSubKeyNames(); } catch { return Array.Empty<string>(); }
    }

    private RegistryIssueGroup ScanEmptyKeys()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.EmptyKeys,
            Title = "Empty software keys",
            Description = "Keys under HKCU\\Software\\<Vendor> with no values and no sub-keys – left behind by uninstallers.",
        };
        using var software = Open(RegistryHive.CurrentUser, RegistryView.Default, "Software");
        if (software is null) return g;
        foreach (var vendor in software.GetSubKeyNames())
        {
            if (RegistryCleanerRules.IsProtectedSoftwareKey(vendor)) continue;
            using var vk = software.OpenSubKey(vendor);
            if (vk is null) continue;
            if (vk.ValueCount == 0 && vk.SubKeyCount == 0)
            {
                g.Items.Add(KeyItem(RegistryHive.CurrentUser, RegistryView.Default, $@"Software\{vendor}", "Empty key", safe: true, g.Title));
                continue;
            }
            foreach (var product in SafeSubKeyNames(vk))
            {
                if (RegistryCleanerRules.IsProtectedSoftwareKey(product)) continue;
                using var pk = vk.OpenSubKey(product);
                if (pk is null || pk.ValueCount != 0 || pk.SubKeyCount != 0) continue;
                g.Items.Add(KeyItem(RegistryHive.CurrentUser, RegistryView.Default, $@"Software\{vendor}\{product}", "Empty key", safe: true, g.Title));
            }
        }
        return g;
    }

    // ───────────────────────────── advanced checks ─────────────────────────────

    private RegistryIssueGroup ScanComObjects()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.ComObjects,
            Title = "COM / ActiveX objects with missing files",
            Description = "Class registrations (CLSID) whose DLL or EXE is gone. Registrations of Windows components are never listed. Advanced – a wrong guess can break a shell extension, so nothing is ticked.",
            Advanced = true,
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view, rootPath) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Classes\CLSID"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\CLSID"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Classes\CLSID"),
                 })
        {
            using var root = Open(hive, view, rootPath);
            if (root is null) continue;
            foreach (var clsid in root.GetSubKeyNames())
            {
                if (!UninstallCommandParser.IsGuid(clsid)) continue;
                using var ck = root.OpenSubKey(clsid);
                if (ck is null) continue;
                var targets = new List<string>();
                bool unjudgeable = false;
                foreach (var server in new[] { "InprocServer32", "LocalServer32", "InprocHandler32" })
                {
                    using var sk = ck.OpenSubKey(server);
                    if (sk is null) continue;
                    var t = RegistryCleanerRules.MainTarget(DefaultString(sk));
                    if (t is null) { unjudgeable = true; break; }
                    targets.Add(t);
                }
                if (unjudgeable || targets.Count == 0) continue;
                if (targets.Any(t => RegistryCleanerRules.IsUnderWindows(t, _windowsDir) || !Missing(t))) continue;
                var sub = $@"{rootPath}\{clsid}";
                if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, sub))) continue;
                var name = DefaultString(ck);
                g.Items.Add(KeyItem(hive, view, sub, $"{(string.IsNullOrWhiteSpace(name) ? clsid : name)} – missing {targets[0]}", safe: false, g.Title));
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanTypeLibraries()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.TypeLibraries,
            Title = "Type libraries with missing files",
            Description = "COM type-library versions whose .tlb / .dll files are all gone. Windows' own libraries are never listed. Advanced – nothing is ticked.",
            Advanced = true,
        };
        var seen = new HashSet<string>();
        foreach (var (hive, view, rootPath) in new[]
                 {
                     (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Classes\TypeLib"),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes\TypeLib"),
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Classes\TypeLib"),
                 })
        {
            using var root = Open(hive, view, rootPath);
            if (root is null) continue;
            foreach (var lib in root.GetSubKeyNames())
            {
                if (!UninstallCommandParser.IsGuid(lib)) continue;
                using var lk = root.OpenSubKey(lib);
                if (lk is null) continue;
                foreach (var ver in SafeSubKeyNames(lk))
                {
                    using var vk = lk.OpenSubKey(ver);
                    if (vk is null) continue;
                    var files = new List<string?>();
                    foreach (var lcid in SafeSubKeyNames(vk))
                    {
                        if (!int.TryParse(lcid, System.Globalization.NumberStyles.HexNumber, null, out _)) continue;
                        using var lcidKey = vk.OpenSubKey(lcid);
                        if (lcidKey is null) continue;
                        foreach (var platform in new[] { "win32", "win64", "arm64" })
                        {
                            using var pk = lcidKey.OpenSubKey(platform);
                            if (pk is null) continue;
                            var raw = DefaultString(pk)?.Trim().Trim('"');
                            files.Add(RegistryCleanerRules.IsAbsoluteLocal(raw) ? raw : null);
                        }
                    }
                    if (files.Count == 0 || files.Any(f => f is null || RegistryCleanerRules.IsUnderWindows(f, _windowsDir) || !Missing(f))) continue;
                    var sub = $@"{rootPath}\{lib}\{ver}";
                    if (Seen(seen, hive, RegFileFormat.KeyPath(hive, view, sub))) continue;
                    var name = DefaultString(vk);
                    g.Items.Add(KeyItem(hive, view, sub, $"{(string.IsNullOrWhiteSpace(name) ? lib : name)} {ver} – missing {files[0]}", safe: false, g.Title));
                }
            }
        }
        return g;
    }

    private RegistryIssueGroup ScanInstallerFolders()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.InstallerFolders,
            Title = "Windows Installer folder records",
            Description = "Folders Windows Installer remembers as created by MSI packages but that no longer exist. Advanced – nothing is ticked; needs administrator rights.",
            Advanced = true,
        };
        using var k = Open(RegistryHive.LocalMachine, RegistryView.Registry64, InstallerFoldersPath);
        if (k is null) return g;
        foreach (var name in k.GetValueNames())
        {
            if (!Missing(name)) continue;
            g.Items.Add(ValueItem(RegistryHive.LocalMachine, RegistryView.Registry64, InstallerFoldersPath, name, "Folder missing", safe: false, g.Title));
        }
        return g;
    }

    private RegistryIssueGroup ScanPrivacyTraces()
    {
        var g = new RegistryIssueGroup
        {
            Category = RegistryIssueCategory.PrivacyTraces,
            Title = "History lists (privacy)",
            Description = "Not errors – lists of what you opened, ran, typed or searched. Windows starts them again empty. Clearing program-usage statistics also clears the \"last used\" dates Evict shows.",
            Advanced = true,
            IsPrivacy = true,
        };
        var hkcu = RegistryHive.CurrentUser;
        var view = RegistryView.Default;
        foreach (var (sub, label) in new[]
                 {
                     ($@"{ExplorerPath}\RecentDocs", "Recently opened documents"),
                     ($@"{ExplorerPath}\RunMRU", "Run dialog history (Win+R)"),
                     ($@"{ExplorerPath}\TypedPaths", "Addresses typed in File Explorer"),
                     ($@"{ExplorerPath}\WordWheelQuery", "File Explorer search history"),
                     ($@"{ExplorerPath}\ComDlg32\OpenSavePidlMRU", "Files picked in Open / Save dialogs"),
                     ($@"{ExplorerPath}\ComDlg32\LastVisitedPidlMRU", "Folders last used in Open / Save dialogs"),
                     (@"Software\Microsoft\Windows\CurrentVersion\Applets\Paint\Recent File List", "Paint recent files"),
                     (@"Software\Microsoft\Windows\CurrentVersion\Applets\Wordpad\Recent File List", "WordPad recent files"),
                 })
        {
            using var k = Open(hkcu, view, sub);
            if (k is null) continue;
            int n = k.ValueCount + k.SubKeyCount;
            if (n == 0) continue;
            g.Items.Add(KeyItem(hkcu, view, sub, $"{label} · {n} entr{(n == 1 ? "y" : "ies")}", safe: false, g.Title));
        }
        using (var regedit = Open(hkcu, view, @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
        {
            if (regedit?.GetValue("LastKey") is string last && last.Length > 0)
                g.Items.Add(ValueItem(hkcu, view, @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", "LastKey", "Registry Editor's last opened key", safe: false, g.Title));
        }
        using (var ua = Open(hkcu, view, $@"{ExplorerPath}\UserAssist"))
        {
            if (ua != null)
            {
                foreach (var guid in ua.GetSubKeyNames())
                {
                    using var count = ua.OpenSubKey(guid + @"\Count");
                    if (count is null || count.ValueCount == 0) continue;
                    g.Items.Add(KeyItem(hkcu, view, $@"{ExplorerPath}\UserAssist\{guid}\Count", $"Program usage statistics (UserAssist) · {count.ValueCount} entries", safe: false, g.Title));
                }
            }
        }
        return g;
    }
}
