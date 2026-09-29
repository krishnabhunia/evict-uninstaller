using System.Globalization;
using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public sealed class ProgramsQueryOptions
{
    public bool IncludeSystemComponents { get; set; }
    public bool IncludeUpdates { get; set; }
    public bool MeasureMissingSizes { get; set; } = true;
    public bool ReadUsageData { get; set; } = true;
}

/// <summary>
/// Enumerates the classic "Programs and Features" list from the three Uninstall registry roots
/// and enriches it with size, usage and bundleware heuristics.
/// </summary>
public sealed class InstalledProgramsService
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly string[] UpdateReleaseTypes = { "Update", "Hotfix", "Security Update", "Service Pack", "Update Rollup" };

    public async Task<List<InstalledProgram>> GetProgramsAsync(ProgramsQueryOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        progress?.Report(new ProgressReport("Reading installed programs from the registry…", 5));
        var list = await Task.Run(() => Enumerate(options), ct).ConfigureAwait(false);

        if (options.ReadUsageData)
        {
            progress?.Report(new ProgressReport("Reading usage statistics…", 35));
            await Task.Run(() => ApplyUsageData(list), ct).ConfigureAwait(false);
        }

        progress?.Report(new ProgressReport("Detecting bundled software…", 45));
        BundlewareDetector.Apply(list);
        KnownBundleware.Apply(list);

        if (options.MeasureMissingSizes)
        {
            var missing = list.Where(p => p.SizeBytes is null && !string.IsNullOrEmpty(p.InstallLocation)).ToList();
            int done = 0;
            await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (p, token) =>
            {
                var size = await Task.Run(() => DirectorySizeCalculator.Measure(p.InstallLocation, token), token).ConfigureAwait(false);
                if (size is { } s)
                {
                    p.SizeBytes = s;
                    p.SizeIsMeasured = true;
                }
                var n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == missing.Count)
                    progress?.Report(new ProgressReport($"Measuring folder sizes… ({n}/{missing.Count})", 50 + 50.0 * n / Math.Max(1, missing.Count)));
            }).ConfigureAwait(false);
        }

        progress?.Report(new ProgressReport("Done", 100));
        return list;
    }

    /// <summary>Synchronous registry walk. Never throws for a single bad key.</summary>
    public List<InstalledProgram> Enumerate(ProgramsQueryOptions options)
    {
        var result = new List<InstalledProgram>(256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view, scope) in Roots())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;

                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(keyName);
                        if (key is null) continue;
                        var program = ReadProgram(key, keyName, scope, options);
                        if (program is null) continue;

                        // The same MSI product may appear under both HKLM views; dedupe by name+version+key.
                        var dedupe = $"{program.DisplayName}|{program.DisplayVersion}|{program.KeyName}";
                        if (!seen.Add(dedupe)) continue;
                        result.Add(program);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Skipping registry key {keyName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Cannot open uninstall root {hive}/{view}: {ex.Message}");
            }
        }

        return result.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View, RegistryScope Scope)> Roots()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryScope.Machine64);
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryScope.Machine32);
        yield return (RegistryHive.CurrentUser, RegistryView.Registry64, RegistryScope.User);
    }

    internal static string RegistryPathFor(RegistryScope scope, string keyName) => scope switch
    {
        RegistryScope.Machine64 => $@"HKEY_LOCAL_MACHINE\{UninstallPath}\{keyName}",
        RegistryScope.Machine32 => $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{keyName}",
        _ => $@"HKEY_CURRENT_USER\{UninstallPath}\{keyName}",
    };

    private static InstalledProgram? ReadProgram(RegistryKey key, string keyName, RegistryScope scope, ProgramsQueryOptions options)
    {
        var displayName = (key.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrWhiteSpace(displayName)) return null;

        bool isSystemComponent = ToInt(key.GetValue("SystemComponent")) == 1;
        if (isSystemComponent && !options.IncludeSystemComponents) return null;

        // Windows / Office updates and patches
        var releaseType = key.GetValue("ReleaseType") as string;
        var parentKey = key.GetValue("ParentKeyName") as string;
        bool isUpdate = !string.IsNullOrEmpty(parentKey)
                        || (releaseType != null && UpdateReleaseTypes.Contains(releaseType, StringComparer.OrdinalIgnoreCase))
                        || LooksLikeUpdate(displayName);
        if (isUpdate && !options.IncludeUpdates) return null;

        var uninstallString = key.GetValue("UninstallString") as string;
        var quietUninstall = key.GetValue("QuietUninstallString") as string;
        bool windowsInstaller = ToInt(key.GetValue("WindowsInstaller")) == 1;
        bool keyIsGuid = UninstallCommandParser.IsGuid(keyName);
        bool isMsi = windowsInstaller || (keyIsGuid && (uninstallString?.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ?? false));

        var installLocation = PathUtil.Clean(key.GetValue("InstallLocation") as string);
        var displayIcon = key.GetValue("DisplayIcon") as string;

        var program = new InstalledProgram
        {
            Id = $"{scope}:{keyName}",
            KeyName = keyName,
            Scope = scope,
            RegistryPath = RegistryPathFor(scope, keyName),
            DisplayName = displayName,
            DisplayVersion = (key.GetValue("DisplayVersion") as string)?.Trim(),
            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
            InstallLocation = installLocation,
            InstallSource = PathUtil.Clean(key.GetValue("InstallSource") as string),
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstall,
            ModifyPath = key.GetValue("ModifyPath") as string,
            DisplayIcon = displayIcon,
            UrlInfoAbout = key.GetValue("URLInfoAbout") as string,
            HelpLink = key.GetValue("HelpLink") as string,
            Comments = key.GetValue("Comments") as string,
            IsSystemComponent = isSystemComponent,
            IsMsi = isMsi,
            MsiProductCode = isMsi && keyIsGuid ? keyName : ExtractGuid(uninstallString),
            InstallDate = ParseInstallDate(key.GetValue("InstallDate")),
            RegistryKeyLastWrite = NativeMethods.GetRegistryKeyLastWriteTime(key),
        };

        // Size: EstimatedSize is in KB.
        var est = key.GetValue("EstimatedSize");
        long kb = est is int i ? i : est is long l ? l : 0;
        if (kb > 0) program.SizeBytes = kb * 1024L;

        // Fill InstallLocation from the uninstaller / icon path when missing.
        if (string.IsNullOrEmpty(program.InstallLocation))
            program.InstallLocation = GuessInstallLocation(program);

        program.PrimaryExecutable = GuessPrimaryExecutable(program);
        program.Installer = isMsi
            ? InstallerKind.Msi
            : UninstallCommandParser.DetectInstaller(UninstallCommandParser.Parse(uninstallString)?.FileName, ReadHead);

        program.IsBrokenEntry = DetectBroken(program);
        return program;
    }

    private static byte[]? ReadHead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(fs.Length, 512 * 1024)];
            int read = fs.Read(buf, 0, buf.Length);
            return read == buf.Length ? buf : buf[..read];
        }
        catch { return null; }
    }

    private static bool LooksLikeUpdate(string name)
    {
        return name.StartsWith("Security Update for", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Update for", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Hotfix for", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Service Pack", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\(KB\d{6,7}\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static int ToInt(object? v) => v switch { int i => i, long l => (int)l, string s when int.TryParse(s, out var r) => r, _ => 0 };

    private static string? ExtractGuid(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var m = UninstallCommandParser.GuidRegex().Match(s);
        return m.Success ? m.Value : null;
    }

    internal static DateTime? ParseInstallDate(object? value)
    {
        if (value is null) return null;
        if (value is int i && i > 0) // some vendors store a unix timestamp
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(i).LocalDateTime; } catch { return null; }
        }
        var s = value.ToString()?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        string[] formats = { "yyyyMMdd", "yyyy-MM-dd", "yyyy/MM/dd", "MM/dd/yyyy", "dd/MM/yyyy", "yyyyMMddHHmmss", "yyyy-MM-ddTHH:mm:ss" };
        if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)) return dt;
        if (DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt)) return dt;
        return null;
    }

    private static string? GuessInstallLocation(InstalledProgram p)
    {
        // Prefer the folder of the DisplayIcon executable (usually the app itself), then the uninstaller folder.
        var (iconPath, _) = PathUtil.SplitIconPath(p.DisplayIcon);
        if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
        {
            var dir = Path.GetDirectoryName(iconPath);
            if (dir != null && !IsSharedSystemFolder(dir)) return dir;
        }
        var cmd = UninstallCommandParser.Parse(p.UninstallString);
        if (cmd != null && !UninstallCommandParser.IsMsiExec(cmd) && !cmd.FileName.Contains("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var dir = Path.GetDirectoryName(cmd.FileName);
                if (dir != null && Directory.Exists(dir) && !IsSharedSystemFolder(dir)) return dir;
            }
            catch { /* invalid path chars */ }
        }
        return null;
    }

    private static bool IsSharedSystemFolder(string dir)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var d = PathUtil.NormalizeForCompare(dir);
        if (PathUtil.IsUnder(d, windows)) return true;
        if (PathUtil.Depth(d) <= 1) return true; // C:\ or C:\Program Files itself
        var leaf = Path.GetFileName(d);
        return leaf.Equals("Common Files", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Temp", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GuessPrimaryExecutable(InstalledProgram p)
    {
        var (iconPath, _) = PathUtil.SplitIconPath(p.DisplayIcon);
        if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath)
            && !Path.GetFileName(iconPath).Contains("unins", StringComparison.OrdinalIgnoreCase))
            return iconPath;

        if (!string.IsNullOrEmpty(p.InstallLocation) && Directory.Exists(p.InstallLocation))
        {
            try
            {
                var exes = new DirectoryInfo(p.InstallLocation)
                    .EnumerateFiles("*.exe", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false })
                    .Where(f => !f.Name.Contains("unins", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.Contains("update", StringComparison.OrdinalIgnoreCase)
                             && !f.Name.Contains("crash", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => NameNormalizer.ToKey(Path.GetFileNameWithoutExtension(f.Name)) == NameNormalizer.ToKey(p.DisplayName))
                    .ThenByDescending(f => f.Length)
                    .ToList();
                if (exes.Count > 0) return exes[0].FullName;
            }
            catch { /* ignore */ }
        }
        if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath)) return iconPath;
        return null;
    }

    private static bool DetectBroken(InstalledProgram p)
    {
        if (p.IsMsi) return false; // msiexec can still remove it (or not – MSI handles its own validation)
        bool folderMissing = string.IsNullOrEmpty(p.InstallLocation) || !Directory.Exists(p.InstallLocation);
        var cmd = UninstallCommandParser.Parse(p.UninstallString);
        bool uninstallerMissing = cmd is null || (!UninstallCommandParser.IsMsiExec(cmd)
                                                  && !cmd.FileName.Contains("rundll32", StringComparison.OrdinalIgnoreCase)
                                                  && IsRooted(cmd.FileName) && !File.Exists(cmd.FileName));
        return folderMissing && uninstallerMissing;
    }

    private static bool IsRooted(string s)
    {
        try { return Path.IsPathRooted(s); } catch { return false; }
    }

    // ───────────────────────────── usage ─────────────────────────────

    private static void ApplyUsageData(List<InstalledProgram> programs)
    {
        var entries = UserAssistReader.Read();
        if (entries.Count == 0) return;

        foreach (var p in programs)
        {
            DateTime? last = null;
            int runs = 0;
            foreach (var e in entries)
            {
                bool match = (!string.IsNullOrEmpty(p.InstallLocation) && PathUtil.IsUnder(e.Path, p.InstallLocation))
                             || (!string.IsNullOrEmpty(p.PrimaryExecutable) && e.Path.Equals(p.PrimaryExecutable, StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                runs += e.RunCount;
                if (e.LastRun is { } lr && (last is null || lr > last)) last = lr;
            }
            if (runs > 0 || last != null)
            {
                p.RunCount = runs;
                p.LastUsed = last;
            }
        }
    }

    // ───────────────────────────── categorisation helpers ─────────────────────────────

    public static bool IsRecentlyInstalled(InstalledProgram p, int days) =>
        p.EffectiveInstallDate is { } d && d >= DateTime.Now.AddDays(-days);

    public static bool IsLarge(InstalledProgram p, int thresholdMb) =>
        p.SizeBytes is { } s && s >= thresholdMb * SizeFormatter.MB;

    /// <summary>Installed a while ago and either never launched or not launched for <paramref name="days"/> days.</summary>
    public static bool IsInfrequentlyUsed(InstalledProgram p, int days)
    {
        var cutoff = DateTime.Now.AddDays(-days);
        if (p.EffectiveInstallDate is { } inst && inst > cutoff) return false; // too new to judge
        if (p.LastUsed is { } lu) return lu < cutoff;
        return p.RunCount == 0; // never seen running
    }
}
