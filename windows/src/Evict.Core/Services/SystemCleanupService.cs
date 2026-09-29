using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public enum CleanupCategory
{
    InstallerCache,      // orphaned .msi/.msp in C:\Windows\Installer
    StoreAppData,        // %LocalAppData%\Packages\<family> of Store apps that are no longer installed
    UpdateCache,         // C:\Windows\SoftwareDistribution\Download
    DeliveryOptimization,// Delivery Optimization peer cache
    WindowsTemp,         // C:\Windows\Temp
    UserTemp,            // %TEMP%
    ErrorReports,        // Windows Error Reporting queues (user + machine)
    CrashDumps,          // C:\Windows\Minidump, MEMORY.DMP, %LocalAppData%\CrashDumps
    WindowsOld,          // C:\Windows.old (report + Storage settings)
    InstallationFiles,   // setup packages in Downloads / Desktop (Recycle Bin)
    RedundantFiles,      // caches, logs, crash reports of installed programs in AppData / ProgramData
}

public sealed class CleanupItem
{
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public string? Detail { get; init; }
    public LeftoverConfidence Confidence { get; init; } = LeftoverConfidence.High;
}

public sealed class CleanupGroup
{
    public required CleanupCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>Items live in the user's own folders (Downloads, Desktop): move them to the Recycle Bin instead of deleting.</summary>
    public bool UseRecycleBin { get; init; }
    /// <summary>Null = clean by deleting the listed items; otherwise a special action described here (e.g. Storage settings).</summary>
    public string? SpecialAction { get; init; }
    public List<CleanupItem> Items { get; } = new();
    public long TotalSize => Items.Sum(i => i.Size);
    public string? Error { get; set; }
    public string? Note { get; set; }
}

public sealed class SystemCleanupResult
{
    public int Removed { get; set; }
    public int Failed { get; set; }
    public long BytesFreed { get; set; }
    /// <summary>Installer-cache packages moved to the backup folder (still on the disk until that folder is deleted).</summary>
    public long BytesMoved { get; set; }
    public List<string> Errors { get; } = new();
    public string? BackupFolder { get; set; }
}

/// <summary>Pure logic for the Windows Installer cache check (unit tested).</summary>
public static class InstallerCacheLogic
{
    /// <summary>Files under C:\Windows\Installer that no product/patch refers to via its LocalPackage value.</summary>
    public static List<string> FindOrphans(IEnumerable<string> cacheFiles, IEnumerable<string> referencedLocalPackages)
    {
        var referenced = new HashSet<string>(referencedLocalPackages.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Norm), StringComparer.OrdinalIgnoreCase);
        var referencedNames = new HashSet<string>(referenced.Select(PathUtil.LeafName), StringComparer.OrdinalIgnoreCase);
        var orphans = new List<string>();
        foreach (var f in cacheFiles)
        {
            var n = Norm(f);
            if (referenced.Contains(n)) continue;
            // Some products store just the file name or a path on another drive letter – match by name too, to be safe.
            if (referencedNames.Contains(PathUtil.LeafName(n))) continue;
            orphans.Add(f);
        }
        return orphans;
    }

    public static bool IsCacheFile(string fileName)
        => fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".msp", StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) => p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}

/// <summary>
/// Finds and removes system-level junk that ordinary uninstallers never touch. Every category reports its size first;
/// nothing is removed until the caller asks. Categories that need administrator rights say so.
/// </summary>
public sealed class SystemCleanupService
{
    private static string WinDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SystemDrive => Path.GetPathRoot(WinDir) ?? "C:\\";
    public static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Evict", "InstallerCacheBackup");

    /// <summary>Folders whose *contents* this service is allowed to delete outright.</summary>
    private static string[] CleanupRoots => new[]
    {
        Path.Combine(WinDir, "Temp"),
        Path.Combine(WinDir, "SoftwareDistribution", "Download"),
        Path.Combine(WinDir, "Minidump"),
        Path.GetTempPath().TrimEnd('\\'),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER"),
    };

    /// <param name="programs">Installed programs (for installation / redundant files); null = read them here.</param>
    /// <param name="only">Scan just these categories (Software Health opens the window this way); null = all.</param>
    public async Task<List<CleanupGroup>> ScanAsync(IReadOnlyCollection<string>? installedPackageFamilies, IProgress<ProgressReport>? progress, CancellationToken ct,
        IReadOnlyList<InstalledProgram>? programs = null, IReadOnlyCollection<CleanupCategory>? only = null)
    {
        if (only is { Count: > 0 }) return await ScanOnlyAsync(only, programs, installedPackageFamilies, progress, ct).ConfigureAwait(false);
        var groups = new List<CleanupGroup>();
        bool admin = ElevationHelper.IsElevated;

        progress?.Report(new ProgressReport("Windows Installer cache…", 5));
        groups.Add(await Task.Run(() => ScanInstallerCache(admin, ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Store app data…", 20));
        groups.Add(await Task.Run(() => ScanStoreAppData(installedPackageFamilies, ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Windows Update download cache…", 35));
        groups.Add(await Task.Run(() => FolderGroup(CleanupCategory.UpdateCache, "Windows Update download cache", "Installer files Windows Update already applied. Safe to remove; Windows downloads again what it still needs.",
            Path.Combine(WinDir, "SoftwareDistribution", "Download"), admin: true, olderThan: TimeSpan.FromDays(1), ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Delivery Optimization cache…", 45));
        groups.Add(await Task.Run(() => ScanDeliveryOptimization(ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Temporary files…", 55));
        groups.Add(await Task.Run(() => FolderGroup(CleanupCategory.WindowsTemp, "Windows temporary files", @"C:\Windows\Temp – left by installers and services. Files changed in the last 24 hours are kept.",
            Path.Combine(WinDir, "Temp"), admin: true, olderThan: TimeSpan.FromHours(24), ct), ct).ConfigureAwait(false));
        groups.Add(await Task.Run(() => FolderGroup(CleanupCategory.UserTemp, "Your temporary files", "%TEMP% of your user account. Files changed in the last 24 hours are kept; files in use are skipped.",
            Path.GetTempPath(), admin: false, olderThan: TimeSpan.FromHours(24), ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Error reports and crash dumps…", 75));
        groups.Add(await Task.Run(() => ScanErrorReports(admin, ct), ct).ConfigureAwait(false));
        groups.Add(await Task.Run(() => ScanCrashDumps(admin, ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Previous Windows installation…", 85));
        groups.Add(await Task.Run(() => ScanWindowsOld(ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Setup files and program caches…", 90));
        programs ??= await Task.Run(ReadPrograms, ct).ConfigureAwait(false);
        groups.Add(await Task.Run(() => ScanInstallationFiles(programs, ct), ct).ConfigureAwait(false));
        groups.Add(await Task.Run(() => ScanRedundantFiles(programs, ct), ct).ConfigureAwait(false));

        progress?.Report(new ProgressReport("Done", 100));
        return groups;
    }

    private async Task<List<CleanupGroup>> ScanOnlyAsync(IReadOnlyCollection<CleanupCategory> only, IReadOnlyList<InstalledProgram>? programs,
        IReadOnlyCollection<string>? families, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        // The two per-program categories are the ones opened on their own; anything else falls back to a full scan.
        if (only.Any(c => c is not (CleanupCategory.InstallationFiles or CleanupCategory.RedundantFiles)))
            return (await ScanAsync(families, progress, ct, programs).ConfigureAwait(false)).Where(g => only.Contains(g.Category)).ToList();
        progress?.Report(new ProgressReport("Reading installed programs…", 10));
        programs ??= await Task.Run(ReadPrograms, ct).ConfigureAwait(false);
        var groups = new List<CleanupGroup>();
        if (only.Contains(CleanupCategory.InstallationFiles))
        {
            progress?.Report(new ProgressReport("Looking for setup files…", 30));
            groups.Add(await Task.Run(() => ScanInstallationFiles(programs, ct), ct).ConfigureAwait(false));
        }
        if (only.Contains(CleanupCategory.RedundantFiles))
        {
            progress?.Report(new ProgressReport("Measuring program caches and logs…", 60));
            groups.Add(await Task.Run(() => ScanRedundantFiles(programs, ct), ct).ConfigureAwait(false));
        }
        progress?.Report(new ProgressReport("Done", 100));
        return groups;
    }

    private static List<InstalledProgram> ReadPrograms() =>
        new InstalledProgramsService().Enumerate(new ProgramsQueryOptions { MeasureMissingSizes = false, ReadUsageData = false });

    // ───────────────────────────── categories ─────────────────────────────

    /// <summary>The Downloads folder (it can be moved), else %USERPROFILE%\Downloads.</summary>
    public static string DownloadsFolder
    {
        get
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                if (k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string s && s.Length > 0) return Environment.ExpandEnvironmentVariables(s);
            }
            catch { /* fall back */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>
    /// Setup packages (.msi, .msix, setup .exe, setup archives) in Downloads (and one sub-folder level) and on the Desktop.
    /// Ticked: the program is installed, or the file is older than 30 days. Newer unknown setups are listed unticked.
    /// Everything goes to the Recycle Bin.
    /// </summary>
    public CleanupGroup ScanInstallationFiles(IReadOnlyList<InstalledProgram> programs, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.InstallationFiles,
            Title = "Installation files",
            Description = "Setup packages left in Downloads and on the Desktop. Ticked when the program is already installed or the file is more than 30 days old. Removed files go to the Recycle Bin.",
            UseRecycleBin = true,
        };
        var names = programs.Select(p => p.DisplayName).ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new[]
        {
            (DownloadsFolder, 1),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 0),
        };
        foreach (var (root, depth) in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true }).ToList(); }
            catch (Exception ex) { g.Error = ex.Message; continue; }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(f)) continue;
                string? description = null, product = null;
                if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(f); description = vi.FileDescription; product = vi.ProductName; }
                    catch { /* no version resource */ }
                }
                var why = InstallationFileRules.InstallerReason(f, description);
                if (why is null) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); } catch { continue; }
                var installed = InstallationFileRules.MatchInstalled(InstallationFileRules.ProductGuess(f, product, description), names);
                var age = DateTime.Now - fi.LastWriteTime;
                var confidence = installed != null ? LeftoverConfidence.High : age.TotalDays > 30 ? LeftoverConfidence.Medium : LeftoverConfidence.Low;
                g.Items.Add(new CleanupItem
                {
                    Path = f, Size = fi.Length, Confidence = confidence,
                    Detail = installed != null ? $"{why} · {installed} is installed" : $"{why} · {(int)age.TotalDays} days old",
                });
            }
        }
        g.Note = g.Items.Count == 0 ? null : "Unticked files are recent setups for programs that are not installed – you may still need them.";
        return g;
    }

    /// <summary>
    /// Cache, log, crash-report and temp folders of installed programs under %LocalAppData%, %AppData%, LocalLow and
    /// (as administrator) ProgramData. Programs recreate them; files in use are skipped.
    /// </summary>
    public CleanupGroup ScanRedundantFiles(IReadOnlyList<InstalledProgram> programs, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.RedundantFiles,
            Title = "Software redundant files",
            Description = "Caches, logs, crash reports and temporary folders of installed programs. They are recreated when needed; close a program first to clear its cache completely. Folders smaller than 1 MB are not listed.",
        };
        var installed = programs.Select(p => (p.DisplayName, p.Publisher)).ToList();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new List<string>
        {
            local,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
        };
        if (ElevationHelper.IsElevated) roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var top in SafeDirs(root))
            {
                ct.ThrowIfCancellationRequested();
                var topName = PathUtil.LeafName(top);
                if (RedundantFileRules.IsSkippedRoot(topName)) continue;
                FindRedundant(top, root, installed, depth: 0, g, ct);
            }
        }
        return g;
    }

    private const long MinRedundantBytes = 1024 * 1024;

    private void FindRedundant(string dir, string root, IReadOnlyList<(string DisplayName, string? Publisher)> installed, int depth, CleanupGroup g, CancellationToken ct)
    {
        if (depth > 5) return;
        foreach (var sub in SafeDirs(dir))
        {
            ct.ThrowIfCancellationRequested();
            var name = PathUtil.LeafName(sub);
            var kind = RedundantFileRules.KindOf(name, PathUtil.LeafName(dir));
            if (kind is null) { FindRedundant(sub, root, installed, depth + 1, g, ct); continue; }
            var segments = Path.GetRelativePath(root, sub).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var owner = RedundantFileRules.OwnerOf(segments, installed);
            if (owner is null) continue; // not an installed program's folder – Residual Cleaner's business
            long size = DirectorySizeCalculator.Measure(sub) ?? 0;
            if (size < MinRedundantBytes) continue;
            var confidence = LeftoverConfidence.High;
            if (kind == "logs" && !RedundantFileRules.OnlyLogFiles(SampleFiles(sub))) confidence = LeftoverConfidence.Low; // may hold chat history, exports…
            g.Items.Add(new CleanupItem { Path = sub, IsDirectory = true, Size = size, Confidence = confidence,
                Detail = $"{owner} · {kind}" + (confidence == LeftoverConfidence.Low ? " (not only log files – review)" : "") });
        }
    }

    private static IEnumerable<string> SampleFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true }).Take(2000).ToList(); }
        catch { return new[] { "unreadable" }; }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0) // junctions (e.g. "Application Data") would loop
                .Select(d => d.FullName).ToList();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private CleanupGroup ScanInstallerCache(bool admin, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.InstallerCache,
            Title = "Windows Installer cache (orphaned .msi / .msp)",
            Description = @"C:\Windows\Installer keeps a copy of every MSI package and patch so programs can repair or uninstall. Files that no installed product refers to any more are orphans. They are moved to a backup folder first (ProgramData\Evict\InstallerCacheBackup) so nothing is lost if a program still needs one.",
            RequiresAdmin = true,
        };
        var dir = Path.Combine(WinDir, "Installer");
        if (!Directory.Exists(dir)) { g.Note = "Folder not found."; return g; }

        var (referenced, complete) = ReadReferencedLocalPackages();
        if (referenced.Count == 0)
        {
            // Without the list of packages that ARE in use, every file would look orphaned – never propose that.
            g.Error = "The Windows Installer registry data could not be read" + (admin ? "." : " – restart Evict as administrator to check this folder.");
            return g;
        }
        List<string> files;
        try { files = Directory.EnumerateFiles(dir).Where(f => InstallerCacheLogic.IsCacheFile(PathUtil.LeafName(f))).ToList(); }
        catch (Exception ex) { g.Error = "Cannot list the Installer folder: " + ex.Message + (admin ? "" : " (administrator rights needed)"); return g; }

        var orphans = InstallerCacheLogic.FindOrphans(files, referenced);
        foreach (var f in orphans)
        {
            ct.ThrowIfCancellationRequested();
            long size = 0; try { size = new FileInfo(f).Length; } catch { /* ignore */ }
            g.Items.Add(new CleanupItem { Path = f, Size = size, Detail = PathUtil.LeafName(f).EndsWith(".msp", StringComparison.OrdinalIgnoreCase) ? "patch" : "package", Confidence = complete ? LeftoverConfidence.High : LeftoverConfidence.Low });
        }
        g.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        g.Note = $"{files.Count:N0} cached files, {referenced.Count:N0} referenced by installed products/patches" + (complete ? "" : " – some registry data could not be read, so the orphans are marked Review");
        return g;
    }

    /// <summary>All LocalPackage values under Installer\UserData\*\Products|Patches (plus HKCR fallback). complete=false when a SID key was unreadable.</summary>
    private static (List<string> Paths, bool Complete) ReadReferencedLocalPackages()
    {
        var list = new List<string>();
        bool complete = true;
        try
        {
            using var userData = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
            if (userData is null) return (list, false);
            foreach (var sid in userData.GetSubKeyNames())
            {
                foreach (var kind in new[] { "Products", "Patches" })
                {
                    RegistryKey? k = null;
                    try { k = userData.OpenSubKey(sid + "\\" + kind); }
                    catch { complete = false; continue; }
                    if (k is null) continue;
                    using (k)
                    {
                        string[] codes;
                        try { codes = k.GetSubKeyNames(); } catch { complete = false; continue; }
                        foreach (var code in codes)
                        {
                            try
                            {
                                using var props = kind == "Products" ? k.OpenSubKey(code + @"\InstallProperties") : k.OpenSubKey(code);
                                if (props?.GetValue("LocalPackage") is string lp && lp.Length > 0) list.Add(lp);
                            }
                            catch { complete = false; }
                        }
                    }
                }
            }
        }
        catch { complete = false; }
        return (list, complete);
    }

    private CleanupGroup ScanStoreAppData(IReadOnlyCollection<string>? installedFamilies, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.StoreAppData,
            Title = "Data of removed Store apps",
            Description = @"%LocalAppData%\Packages keeps settings and caches of Store / UWP apps. Folders whose app is no longer installed for your account are listed here. Reinstalling the app would start fresh – keep a folder if you plan to reinstall and want the old data.",
            RequiresAdmin = false,
        };
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(dir)) { g.Note = "Folder not found."; return g; }
        if (installedFamilies is null || installedFamilies.Count == 0) { g.Error = "The list of installed Store apps could not be read, so nothing is proposed."; return g; }
        var installed = new HashSet<string>(installedFamilies, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            var name = PathUtil.LeafName(folder);
            if (installed.Contains(name)) continue;
            if (name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("windows.immersivecontrolpanel", StringComparison.OrdinalIgnoreCase))
                continue; // shell components register differently; never propose them
            long size = DirectorySizeCalculator.Measure(folder, ct) ?? 0;
            var app = name.Contains('_') ? name[..name.LastIndexOf('_')] : name;
            g.Items.Add(new CleanupItem { Path = folder, IsDirectory = true, Size = size, Detail = app, Confidence = LeftoverConfidence.Medium });
        }
        g.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return g;
    }

    private CleanupGroup ScanDeliveryOptimization(CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.DeliveryOptimization,
            Title = "Delivery Optimization cache",
            Description = "Update and Store downloads kept for sharing with other PCs. Cleared with Windows' own Delete-DeliveryOptimizationCache command.",
            RequiresAdmin = true,
            SpecialAction = "powershell:Delete-DeliveryOptimizationCache -Force",
        };
        var dir = Path.Combine(WinDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache");
        try
        {
            if (!Directory.Exists(dir)) { g.Note = "No cache folder."; return g; }
            long size = DirectorySizeCalculator.Measure(dir, ct) ?? 0;
            if (size > 0) g.Items.Add(new CleanupItem { Path = dir, IsDirectory = true, Size = size, Detail = "cache folder" });
        }
        catch (Exception ex) { g.Error = ex.Message + (ElevationHelper.IsElevated ? "" : " (administrator rights needed to measure)"); }
        return g;
    }

    private CleanupGroup FolderGroup(CleanupCategory cat, string title, string description, string dir, bool admin, TimeSpan olderThan, CancellationToken ct)
    {
        var g = new CleanupGroup { Category = cat, Title = title, Description = description, RequiresAdmin = admin };
        if (!Directory.Exists(dir)) { g.Note = "Folder not found."; return g; }
        var cutoff = DateTime.UtcNow - olderThan;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    bool isDir = Directory.Exists(entry);
                    var leaf = PathUtil.LeafName(entry);
                    // %TEMP%\.net holds the extracted native libraries of running single-file .NET apps (Evict included).
                    if (isDir && leaf.Equals(".net", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isDir)
                    {
                        // A folder is "old" only when nothing inside it was written recently either.
                        var (size, newest) = MeasureFolder(entry, ct);
                        if (newest > cutoff) continue;
                        if (size == 0 && !DirectorySizeCalculator.IsEmptyDirectory(entry)) continue;
                        g.Items.Add(new CleanupItem { Path = entry, IsDirectory = true, Size = size, Confidence = LeftoverConfidence.Medium });
                    }
                    else
                    {
                        if (File.GetLastWriteTimeUtc(entry) > cutoff) continue;
                        g.Items.Add(new CleanupItem { Path = entry, IsDirectory = false, Size = new FileInfo(entry).Length });
                    }
                }
                catch { /* locked / gone */ }
            }
        }
        catch (Exception ex) { g.Error = ex.Message + (admin && !ElevationHelper.IsElevated ? " (administrator rights needed)" : ""); }
        g.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return g;
    }

    /// <summary>Total size and the newest last-write time of anything inside a folder (one walk).</summary>
    private static (long Size, DateTime NewestWriteUtc) MeasureFolder(string dir, CancellationToken ct)
    {
        long size = 0;
        DateTime newest = DateTime.MinValue;
        try { newest = Directory.GetLastWriteTimeUtc(dir); } catch { /* ignore */ }
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var fi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
                    if (fi is FileInfo f) size += f.Length;
                }
                catch { /* ignore */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* partial */ }
        return (size, newest);
    }

    private CleanupGroup ScanErrorReports(bool admin, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.ErrorReports,
            Title = "Windows Error Reporting files",
            Description = "Queued and archived crash reports (yours under AppData, the machine's under ProgramData – the latter needs administrator rights).",
            RequiresAdmin = false,
        };
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "Temp"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root))
                {
                    ct.ThrowIfCancellationRequested();
                    bool isDir = Directory.Exists(entry);
                    long size = isDir ? (DirectorySizeCalculator.Measure(entry, ct) ?? 0) : new FileInfo(entry).Length;
                    g.Items.Add(new CleanupItem { Path = entry, IsDirectory = isDir, Size = size, Detail = root.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) ? "machine (admin)" : "your account" });
                }
            }
            catch { /* access denied without admin */ }
        }
        g.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return g;
    }

    private CleanupGroup ScanCrashDumps(bool admin, CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.CrashDumps,
            Title = "Crash dumps",
            Description = @"Memory dumps written after blue screens (C:\Windows\Minidump, MEMORY.DMP – administrator) and application crash dumps in your AppData. Only useful if you are debugging a crash.",
            RequiresAdmin = false,
        };
        void AddFile(string p, string detail) { try { if (File.Exists(p)) g.Items.Add(new CleanupItem { Path = p, Size = new FileInfo(p).Length, Detail = detail }); } catch { /* ignore */ } }
        void AddDirFiles(string d, string detail)
        {
            try { if (Directory.Exists(d)) foreach (var f in Directory.EnumerateFiles(d)) { ct.ThrowIfCancellationRequested(); AddFile(f, detail); } }
            catch { /* ignore */ }
        }
        AddFile(Path.Combine(WinDir, "MEMORY.DMP"), "kernel dump (admin)");
        AddDirFiles(Path.Combine(WinDir, "Minidump"), "minidump (admin)");
        AddDirFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"), "application dump");
        g.Items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return g;
    }

    private CleanupGroup ScanWindowsOld(CancellationToken ct)
    {
        var g = new CleanupGroup
        {
            Category = CleanupCategory.WindowsOld,
            Title = "Previous Windows installation (Windows.old)",
            Description = "Left after a Windows upgrade so you can roll back. Windows removes it itself after 10 days; you can remove it now through Storage settings → Temporary files → Previous Windows installation(s).",
            RequiresAdmin = true,
            SpecialAction = "ms-settings:storagesense",
        };
        var dir = Path.Combine(SystemDrive, "Windows.old");
        if (!Directory.Exists(dir)) { g.Note = "Not present."; return g; }
        long size = 0;
        try { size = DirectorySizeCalculator.Measure(dir, ct) ?? 0; } catch { /* partial */ }
        g.Items.Add(new CleanupItem { Path = dir, IsDirectory = true, Size = size, Detail = "open Storage settings to remove", Confidence = LeftoverConfidence.Low });
        return g;
    }

    // ───────────────────────────── cleaning ─────────────────────────────

    public async Task<SystemCleanupResult> CleanAsync(IEnumerable<(CleanupGroup Group, CleanupItem Item)> selection, bool moveInstallerCacheToBackup, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var result = new SystemCleanupResult();
        var list = selection.ToList();
        int done = 0;

        // Windows Update cache: stop the services first, restart afterwards.
        bool touchesUpdateCache = list.Any(s => s.Group.Category == CleanupCategory.UpdateCache);
        if (touchesUpdateCache && ElevationHelper.IsElevated)
        {
            progress?.Report(new ProgressReport("Stopping Windows Update services…", 2));
            await ServiceControl("stop", "wuauserv", ct).ConfigureAwait(false);
            await ServiceControl("stop", "bits", ct).ConfigureAwait(false);
        }

        string? backupDir = null;
        foreach (var (group, item) in list)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ProgressReport($"Removing {PathUtil.LeafName(item.Path)}…", 5 + 90.0 * done / Math.Max(1, list.Count)));
            try
            {
                if (group.SpecialAction is { } special)
                {
                    if (special.StartsWith("powershell:", StringComparison.OrdinalIgnoreCase))
                    {
                        var res = await PowerShellRunner.RunScriptAsync(special["powershell:".Length..], ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
                        if (res.Success) { result.Removed++; result.BytesFreed += item.Size; }
                        else { result.Failed++; result.Errors.Add($"{group.Title}: {res.StdErr.Trim()}"); }
                    }
                    // ms-settings: links are opened by the UI, not here.
                    continue;
                }

                if (group.Category == CleanupCategory.InstallerCache && moveInstallerCacheToBackup)
                {
                    backupDir ??= Path.Combine(BackupRoot, DateTime.Now.ToString("yyyy-MM-dd_HHmm"));
                    Directory.CreateDirectory(backupDir);
                    var target = Path.Combine(backupDir, PathUtil.LeafName(item.Path));
                    if (File.Exists(target)) target = Path.Combine(backupDir, Path.GetFileNameWithoutExtension(target) + "_" + Guid.NewGuid().ToString("N")[..6] + Path.GetExtension(target));
                    File.Move(item.Path, target);
                    result.Removed++; result.BytesMoved += item.Size;
                    result.BackupFolder = backupDir;
                    continue;
                }

                // Items inside the fixed cleanup roots may be deleted even though they live under C:\Windows.
                bool trusted = CleanupRoots.Any(r => PathUtil.IsUnder(item.Path, r));
                var err = LeftoverCleaner.DeletePath(item.Path, item.IsDirectory, new CleanupOptions { SendToRecycleBin = group.UseRecycleBin, ScheduleLockedForReboot = false }, trustedRoot: trusted);
                if (err is null) { result.Removed++; result.BytesFreed += item.Size; }
                else { result.Failed++; result.Errors.Add($"{item.Path}: {err}"); }
            }
            catch (Exception ex) { result.Failed++; result.Errors.Add($"{item.Path}: {ex.Message}"); }
        }

        if (touchesUpdateCache && ElevationHelper.IsElevated)
        {
            progress?.Report(new ProgressReport("Starting Windows Update services…", 97));
            await ServiceControl("start", "bits", ct).ConfigureAwait(false);
            await ServiceControl("start", "wuauserv", ct).ConfigureAwait(false);
        }
        progress?.Report(new ProgressReport("Done", 100));
        Log.Info($"System cleanup: removed {result.Removed}, failed {result.Failed}, freed {SizeFormatter.Format(result.BytesFreed)}");
        return result;
    }

    private static async Task ServiceControl(string verb, string service, CancellationToken ct)
    {
        try { await ProcessRunner.RunCapturedAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "net.exe"), $"{verb} {service}", ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"net {verb} {service}: {ex.Message}"); }
    }
}
