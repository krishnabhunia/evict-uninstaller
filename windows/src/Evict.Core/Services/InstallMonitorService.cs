using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>
/// Install Monitor: watches the file system (FileSystemWatcher on the usual install roots) and diffs a
/// registry snapshot taken before and after an installer runs, producing an <see cref="InstallLog"/> that can
/// later drive a thorough uninstall.
/// </summary>
public sealed class InstallMonitorService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // ───────────────────────────── logs on disk ─────────────────────────────

    public List<InstallLog> LoadLogs()
    {
        var list = new List<InstallLog>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(AppPaths.InstallLogsDir, "*.json"))
            {
                try
                {
                    var log = JsonSerializer.Deserialize<InstallLog>(File.ReadAllText(f), JsonOptions);
                    if (log != null) list.Add(log);
                }
                catch { /* skip corrupt */ }
            }
        }
        catch { /* ignore */ }
        return list.OrderByDescending(l => l.Started).ToList();
    }

    public void SaveLog(InstallLog log)
    {
        var path = Path.Combine(AppPaths.InstallLogsDir, log.Id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(log, JsonOptions));
    }

    public void DeleteLog(InstallLog log)
    {
        try { File.Delete(Path.Combine(AppPaths.InstallLogsDir, log.Id + ".json")); } catch { /* ignore */ }
    }

    // ───────────────────────────── watched roots ─────────────────────────────

    private static IEnumerable<string> WatchRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "drivers");
    }

    /// <summary>Paths that change constantly and are never part of an install worth logging.</summary>
    private static readonly string[] NoiseFragments =
    {
        @"\Temp\", @"\Microsoft\Windows\Explorer\", @"\Microsoft\Windows\INetCache\", @"\Microsoft\Windows\Recent\",
        @"\Microsoft\Windows\WebCache\", @"\Microsoft\Windows\Notifications\", @"\Packages\Microsoft.Windows.", @"\Microsoft\Edge\User Data\",
        @"\Google\Chrome\User Data\", @"\Mozilla\Firefox\Profiles\", @"\Microsoft\Windows\PowerShell\", @"\Microsoft\CLR_v4.0", @"\ConnectedDevicesPlatform\",
        @"\Microsoft\Windows\SchCache\", @"\Microsoft\Windows\Caches\", @"\Microsoft\Windows\History\", @"\Microsoft\Windows\CloudStore\",
        @"\CrashDumps\", @"\D3DSCache\", @"\NVIDIA\", @"\Microsoft\Windows\Themes\", @"\Microsoft\Windows\1033\", @"\Microsoft\Office\16.0\",
        @"\Microsoft\Teams\", @"\Microsoft\OneDrive\logs\", @"\Microsoft\Windows\WER\", @"\Microsoft\Windows\AppRepository\",
        @"\Evict\", @"\Microsoft\Windows\Start Menu\Programs\Startup\desktop.ini", @"\Microsoft\Windows Defender\", @"\Microsoft\Search\",
        @"\Microsoft\Diagnosis\", @"\Microsoft\Network\Downloader\", @"\USOShared\", @"\Microsoft\Windows\SystemData\", @"\Microsoft\Windows\Shell\",
    };

    private static readonly string[] RegistryNoisePrefixes =
    {
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\", @"SOFTWARE\Microsoft\Windows Search\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CloudStore\", @"SOFTWARE\Classes\Local Settings\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Diagnostics\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\", @"SOFTWARE\Microsoft\Windows Defender\", @"SOFTWARE\Microsoft\SecurityManager\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\", @"SOFTWARE\Microsoft\Tracing\",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\", // handled separately
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached",
        @"SOFTWARE\Microsoft\Cryptography\", @"SOFTWARE\Microsoft\SystemCertificates\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\BITS\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\",
        @"SOFTWARE\Microsoft\Input\", @"SOFTWARE\Microsoft\IdentityCRL\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\ImmersiveShell\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Lock Screen\",
        @"SOFTWARE\Microsoft\Windows\Shell\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications\", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Store\",
    };

    private static bool IsNoise(string path) => NoiseFragments.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));

    // ───────────────────────────── session ─────────────────────────────

    public sealed class Session : IDisposable
    {
        private readonly List<FileSystemWatcher> _watchers = new();
        private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _changed = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _registryBefore = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _uninstallBefore = new(StringComparer.OrdinalIgnoreCase);
        public string InstallerPath { get; }
        public DateTime Started { get; } = DateTime.Now;
        public int EventCount => _created.Count + _changed.Count;
        public bool BufferOverflowed { get; private set; }

        public Session(string installerPath) => InstallerPath = installerPath;

        internal void Start(IProgress<ProgressReport>? progress, CancellationToken ct)
        {
            // Watchers first: they start instantly, so fast installers are not missed while the registry is walked.
            progress?.Report(new ProgressReport("Starting file system watchers…", 5));
            foreach (var root in WatchRoots().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    var w = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    };
                    w.Created += (_, e) => Record(_created, e.FullPath);
                    w.Renamed += (_, e) => Record(_created, e.FullPath);
                    w.Changed += (_, e) => { if (!_created.ContainsKey(e.FullPath)) Record(_changed, e.FullPath); };
                    w.Error += (_, _) => BufferOverflowed = true;
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) { Log.Warn($"Watcher for {root}: {ex.Message}"); }
            }

            progress?.Report(new ProgressReport("Taking registry snapshot…", 30));
            _registryBefore = SnapshotRegistry(ct);
            _uninstallBefore = SnapshotUninstallKeys();
        }

        private static void Record(ConcurrentDictionary<string, byte> set, string path)
        {
            if (IsNoise(path)) return;
            set.TryAdd(path, 0);
        }

        internal InstallLog Finish(int? installerExitCode, IProgress<ProgressReport>? progress, CancellationToken ct)
        {
            foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; } catch { /* ignore */ } }

            progress?.Report(new ProgressReport("Comparing registry…", 60));
            var registryAfter = SnapshotRegistry(ct);
            var newKeys = registryAfter.Where(k => !_registryBefore.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            // Collapse children of a new parent key – the parent is enough to delete the tree.
            var collapsedKeys = new List<string>();
            foreach (var k in newKeys)
            {
                if (collapsedKeys.Any(p => k.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                collapsedKeys.Add(k);
            }
            var uninstallAfter = SnapshotUninstallKeys();
            var newUninstall = uninstallAfter.Where(k => !_uninstallBefore.Contains(k)).ToList();

            progress?.Report(new ProgressReport("Collecting file changes…", 85));
            var created = _created.Keys.Where(p => !IsNoise(p)).ToList();
            var dirs = new List<string>();
            var files = new List<string>();
            foreach (var p in created)
            {
                try
                {
                    if (Directory.Exists(p)) dirs.Add(p);
                    else if (File.Exists(p)) files.Add(p);
                }
                catch { /* ignore */ }
            }
            // Keep only top-most new directories; files inside them are implied.
            dirs.Sort(StringComparer.OrdinalIgnoreCase);
            var topDirs = new List<string>();
            foreach (var d in dirs)
            {
                if (topDirs.Any(t => PathUtil.IsUnder(d, t))) continue;
                topDirs.Add(d);
            }
            var looseFiles = files.Where(f => !topDirs.Any(t => PathUtil.IsUnder(f, t))).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            var modified = _changed.Keys.Where(p => !IsNoise(p) && !topDirs.Any(t => PathUtil.IsUnder(p, t)) && File.Exists(p)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

            long total = 0;
            foreach (var d in topDirs) total += DirectorySizeCalculator.Measure(d, ct) ?? 0;
            foreach (var f in looseFiles) { try { total += new FileInfo(f).Length; } catch { /* ignore */ } }

            var title = Path.GetFileNameWithoutExtension(InstallerPath);
            if (newUninstall.Count > 0)
            {
                var friendly = ReadDisplayName(newUninstall[0]);
                if (!string.IsNullOrEmpty(friendly)) title = friendly;
            }

            progress?.Report(new ProgressReport("Done", 100));
            return new InstallLog
            {
                Title = title,
                InstallerPath = InstallerPath,
                Started = Started,
                Finished = DateTime.Now,
                InstallerExitCode = installerExitCode,
                CreatedDirectories = topDirs,
                CreatedFiles = looseFiles,
                ModifiedFiles = modified,
                CreatedRegistryKeys = collapsedKeys,
                NewUninstallEntries = newUninstall,
                TotalBytes = total,
            };
        }

        public void Dispose()
        {
            foreach (var w in _watchers) { try { w.Dispose(); } catch { /* ignore */ } }
            _watchers.Clear();
        }
    }

    // ───────────────────────────── registry snapshots ─────────────────────────────

    private static readonly (RegistryHive Hive, RegistryView View, string Root)[] SnapshotRoots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "SOFTWARE"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"SYSTEM\CurrentControlSet\Services"),
    };

    /// <summary>Set of "HKxx|view|SubKey" strings for every key under the snapshot roots (depth-limited for speed).</summary>
    private static HashSet<string> SnapshotRegistry(CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, root) in SnapshotRoots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var k = baseKey.OpenSubKey(root);
                if (k is null) continue;
                var prefix = $"{RegistryPaths.HiveAbbreviation(hive)}|{(view == RegistryView.Registry32 ? "32" : "64")}|";
                Walk(k, root, prefix, set, depth: 0, maxDepth: root.StartsWith("SYSTEM") ? 1 : 4, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn($"Snapshot {hive}/{view}/{root}: {ex.Message}"); }
        }
        return set;
    }

    private static void Walk(RegistryKey key, string subKeyPath, string prefix, HashSet<string> set, int depth, int maxDepth, CancellationToken ct)
    {
        string[] names;
        try { names = key.GetSubKeyNames(); } catch { return; }
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var childPath = subKeyPath + "\\" + name;
            if (RegistryNoisePrefixes.Any(p => childPath.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (depth == 0 && (name.Equals("Classes", StringComparison.OrdinalIgnoreCase) || name.Equals("Wow6432Node", StringComparison.OrdinalIgnoreCase))) continue;
            set.Add(prefix + childPath);
            if (depth >= maxDepth) continue;
            try
            {
                using var child = key.OpenSubKey(name);
                if (child != null) Walk(child, childPath, prefix, set, depth + 1, maxDepth, ct);
            }
            catch { /* access denied etc. */ }
        }
    }

    private static HashSet<string> SnapshotUninstallKeys()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var k = baseKey.OpenSubKey(UninstallSubKey);
                if (k is null) continue;
                foreach (var n in k.GetSubKeyNames())
                    set.Add($"{RegistryPaths.HiveAbbreviation(hive)}|{(view == RegistryView.Registry32 ? "32" : "64")}|{UninstallSubKey}\\{n}");
            }
            catch { /* ignore */ }
        }
        return set;
    }

    private static string? ReadDisplayName(string snapshotKey)
    {
        var (hive, view, sub) = ParseSnapshotKey(snapshotKey);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(sub);
            return k?.GetValue("DisplayName") as string;
        }
        catch { return null; }
    }

    public static (RegistryHive Hive, RegistryView View, string SubKey) ParseSnapshotKey(string s)
    {
        var parts = s.Split('|', 3);
        var hive = parts[0] == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var view = parts.Length > 1 && parts[1] == "32" ? RegistryView.Registry32 : RegistryView.Registry64;
        return (hive, view, parts.Length > 2 ? parts[2] : "");
    }

    public static string DisplaySnapshotKey(string s)
    {
        var (hive, view, sub) = ParseSnapshotKey(s);
        return RegistryPaths.Display(hive, view, sub);
    }

    // ───────────────────────────── orchestration ─────────────────────────────

    /// <summary>Starts monitoring, launches the installer, waits for it and returns the log. The log is also saved.</summary>
    public async Task<InstallLog> MonitorInstallAsync(string installerPath, string? arguments, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        using var session = new Session(installerPath);
        await Task.Run(() => session.Start(progress, ct), ct).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Installer is running – complete the installation, then return here…", 50));
        int? exitCode = null;
        try
        {
            var psi = new ProcessStartInfo(installerPath, arguments ?? "") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installerPath) ?? "" };
            using var proc = Process.Start(psi);
            if (proc != null)
            {
                ProcessRunner.NotifyStarted(proc.Id);
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                exitCode = proc.ExitCode;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Installer launch failed: " + ex.Message);
        }

        // Installers often hand off to msiexec / a second process – give them a moment to settle.
        progress?.Report(new ProgressReport("Waiting for background installer processes…", 55));
        var settleUntil = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < settleUntil && MsiRunning())
            await Task.Delay(1500, ct).ConfigureAwait(false);
        await Task.Delay(2000, ct).ConfigureAwait(false);

        var log = await Task.Run(() => session.Finish(exitCode, progress, ct), ct).ConfigureAwait(false);
        SaveLog(log);
        return log;
    }

    /// <summary>
    /// Records an installation that is <b>already running</b> (detected by <see cref="InstallerDetector"/>): snapshots now,
    /// waits until the installer's process tree (and any msiexec it hands off to) has exited, then diffs and saves the log.
    /// Changes made in the first seconds before detection are not captured – the log says so in its title suffix.
    /// </summary>
    public async Task<InstallLog> MonitorRunningInstallAsync(DetectedInstaller installer, ProcessTree tree, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        using var session = new Session(installer.ImagePath);
        await Task.Run(() => session.Start(progress, ct), ct).ConfigureAwait(false);

        progress?.Report(new ProgressReport($"Recording {installer.DisplayName} – waiting for the installer to finish…", 50));
        var started = DateTime.UtcNow;
        while (tree.IsAlive())
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            if (DateTime.UtcNow - started > TimeSpan.FromHours(2)) { Log.Warn("Recording stopped after 2 h – installer still running."); break; }
        }
        int? exitCode = null;
        try { using var root = Process.GetProcessById(installer.ProcessId); exitCode = root.HasExited ? root.ExitCode : null; } catch { /* gone */ }

        progress?.Report(new ProgressReport("Waiting for background installer processes…", 55));
        var settleUntil = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < settleUntil && MsiRunning())
            await Task.Delay(1500, ct).ConfigureAwait(false);
        await Task.Delay(2000, ct).ConfigureAwait(false);

        var log = await Task.Run(() => session.Finish(exitCode, progress, ct), ct).ConfigureAwait(false);
        if (log.Title.Equals(Path.GetFileNameWithoutExtension(installer.ImagePath), StringComparison.OrdinalIgnoreCase)) log.Title = installer.DisplayName;
        SaveLog(log);
        return log;
    }

    private static bool MsiRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("msiexec"))
            {
                try { if ((DateTime.Now - p.StartTime) < TimeSpan.FromMinutes(30)) return true; }
                catch { /* system instance */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    /// <summary>Converts an install log into leftover items (used to clean up after running the regular uninstaller).</summary>
    public static List<LeftoverItem> ToLeftovers(InstallLog log)
    {
        var items = new List<LeftoverItem>();
        foreach (var d in log.CreatedDirectories)
        {
            if (!Directory.Exists(d) || PathUtil.IsProtectedRoot(d, checkProtectedNames: false)) continue;
            items.Add(new LeftoverItem { Kind = LeftoverKind.Folder, Path = d, SizeBytes = DirectorySizeCalculator.Measure(d) ?? 0, Confidence = LeftoverConfidence.High, Detail = "Created during monitored install", ProgramName = log.Title });
        }
        foreach (var f in log.CreatedFiles)
        {
            if (!File.Exists(f)) continue;
            var kind = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? LeftoverKind.Shortcut : LeftoverKind.File;
            long size = 0; try { size = new FileInfo(f).Length; } catch { /* ignore */ }
            items.Add(new LeftoverItem { Kind = kind, Path = f, SizeBytes = size, Confidence = LeftoverConfidence.High, Detail = "Created during monitored install", ProgramName = log.Title });
        }
        foreach (var k in log.CreatedRegistryKeys)
        {
            var (hive, view, sub) = ParseSnapshotKey(k);
            if (string.IsNullOrEmpty(sub) || sub.Count(c => c == '\\') < 1) continue;
            items.Add(new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = RegistryPaths.Display(hive, view, sub), Hive = hive, RegView = view, SubKey = sub, Confidence = LeftoverConfidence.High, Detail = "Created during monitored install", ProgramName = log.Title });
        }
        return items;
    }
}
