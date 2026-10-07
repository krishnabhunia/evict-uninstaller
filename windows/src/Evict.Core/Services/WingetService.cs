using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>
/// Software Updater backed by the Windows Package Manager (winget). Lists upgradable packages by
/// parsing the fixed-width table "winget upgrade" prints, and upgrades them one at a time.
/// </summary>
public sealed class WingetService
{
    private readonly Func<string?> _findWinget;
    private readonly Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> _runCaptured;

    public WingetService()
    {
        _findWinget = FindWinget;
        _runCaptured = (exe, args, ct, timeout, onLine) => ProcessRunner.RunCapturedAsync(exe, args, ct, timeout, onLine, Encoding.UTF8);
    }

    internal WingetService(Func<string?> findWinget,
        Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> runCaptured)
    {
        _findWinget = findWinget;
        _runCaptured = runCaptured;
    }

    public static string? FindWinget()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var alias = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            if (File.Exists(alias)) return alias;
            // Elevated processes sometimes lack the per-user alias on PATH – look inside WindowsApps.
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var winApps = Path.Combine(pf, "WindowsApps");
            if (Directory.Exists(winApps))
            {
                var dir = Directory.EnumerateDirectories(winApps, "Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe").OrderByDescending(d => d).FirstOrDefault();
                if (dir != null && File.Exists(Path.Combine(dir, "winget.exe"))) return Path.Combine(dir, "winget.exe");
            }
        }
        catch { /* ignore */ }
        return null;
    }

    public static bool IsAvailable => FindWinget() != null;

    public async Task<(List<UpgradablePackage> Packages, string? Error)> GetUpgradesAsync(bool includeUnknown, CancellationToken ct, Action<string>? onLine = null)
    {
        var winget = _findWinget();
        if (winget is null) return (new(), "winget (App Installer) was not found. Install it from the Microsoft Store to use Software Updater.");
        var args = "upgrade --accept-source-agreements --disable-interactivity" + (includeUnknown ? " --include-unknown" : "");
        var res = await _runCaptured(winget, args, ct, TimeSpan.FromMinutes(4), onLine).ConfigureAwait(false);
        if (res.TimedOut) return (new(), "winget did not respond in time.");
        var parsed = ParseUpgradeOutput(res.StdOut);
        // Discovery has no package filter: these exit codes mean there is no matching installed upgrade.
        bool noUpdates = unchecked((uint)res.ExitCode) is 0x8A150014 or 0x8A15002B;
        if (noUpdates) return (new(), null);
        if (!res.Success) return (parsed.Packages, DescribeFailure(res.ExitCode, res.StdOut + res.StdErr));
        if (parsed.Error != null) return (new(), parsed.Error);
        if (!parsed.Recognized && !res.StdOut.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
            && !res.StdOut.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase))
            return (new(), "winget returned an unrecognized update result. Updates could not be checked; review the winget output or try again.");
        return (parsed.Packages, null);
    }

    private static string FirstUseful(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.All(c => c is '-' or '\\' or '|' or '/' or ' ')) ?? "";

    /// <summary>
    /// Parses ordered table columns by position rather than translated labels.
    /// Offsets use display columns so wide Unicode names do not shift package identities.
    /// </summary>
    public static List<UpgradablePackage> ParseUpgradeTable(string output)
        => ParseUpgradeOutput(output).Packages;

    internal sealed record UpgradeTableResult(List<UpgradablePackage> Packages, bool Recognized, string? Error = null);

    internal static UpgradeTableResult ParseUpgradeOutput(string output)
    {
        var result = new List<UpgradablePackage>();
        if (string.IsNullOrWhiteSpace(output)) return new(result, false);
        var clean = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var lines = Regex.Replace(clean, @"\r+\n|\r", "\n").Split('\n');
        int separator = -1;
        MatchCollection? columns = null;
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length < 10 || !line.All(c => c == '-')) continue;
            var candidate = Regex.Matches(lines[i - 1], @"\S(?:.*?\S)?(?=\s{2,}|$)");
            if (candidate.Count is not (4 or 5)) candidate = Regex.Matches(lines[i - 1], @"\S+");
            if (candidate.Count is not (4 or 5)) continue;
            separator = i;
            columns = candidate;
            break;
        }
        if (separator < 0 || columns is null) return new(result, false);
        var header = lines[separator - 1];
        var starts = columns.Select(m => DisplayWidth(header[..m.Index])).ToArray();
        int idCol = starts[1], verCol = starts[2], availCol = starts[3];
        int srcCol = starts.Length == 5 ? starts[4] : -1;
        for (int i = separator + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { if (result.Count > 0) break; continue; }
            if (line.Trim().All(c => c == '-') || line.Trim() == header.Trim()) break;
            int width = DisplayWidth(line);
            if ((width <= availCol && Regex.IsMatch(line, @"^\s*\d+\s"))
                || line.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase)) break;
            if (width <= availCol) return new(new(), true, "winget returned an incomplete package row. Updates could not be checked safely.");
            string name = SliceColumns(line, 0, idCol);
            string id = SliceColumns(line, idCol, verCol);
            string version = SliceColumns(line, verCol, availCol);
            string available = SliceColumns(line, availCol, srcCol > availCol ? srcCol : width);
            string source = srcCol > availCol ? SliceColumns(line, srcCol, width) : "";
            if (name.Length == 0 || id.Length == 0 || version.Length == 0 || available.Length == 0)
                return new(new(), true, "winget returned an incomplete package row. Updates could not be checked safely.");
            if (id.Contains('\u2026')) return new(new(), true, "winget truncated a package identifier. Updates could not be checked safely; use Windows Package Manager directly.");
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$"))
                return new(new(), true, "winget returned a misaligned package table. Updates could not be checked safely.");
            result.Add(new UpgradablePackage { Name = name, Id = id, InstalledVersion = version, AvailableVersion = available, Source = source });
        }
        return result.Count > 0 ? new(result, true)
            : new(result, true, "winget returned a package table with no readable rows. Updates could not be checked safely.");
    }

    private static int RuneWidth(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format) return 0;
        int n = rune.Value;
        return n is >= 0x1100 and <= 0x115F or >= 0x2329 and <= 0x232A or >= 0x2E80 and <= 0xA4CF
            or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF or >= 0xFE10 and <= 0xFE19
            or >= 0xFE30 and <= 0xFE6F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
    }

    private static int DisplayWidth(string text) => text.EnumerateRunes().Sum(RuneWidth);

    private static string SliceColumns(string line, int start, int end)
    {
        var value = new StringBuilder();
        int column = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (column >= end) break;
            if (column >= start) value.Append(rune.ToString());
            column += RuneWidth(rune);
        }
        return value.ToString().Trim();
    }

    internal static string BuildUpgradeArguments(UpgradablePackage pkg, bool includeUnknown)
    {
        var args = $"upgrade --id \"{pkg.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        if (includeUnknown) args += " --include-unknown";
        if (!string.IsNullOrEmpty(pkg.Source)) args += $" --source \"{pkg.Source.Replace("\"", "\\\"")}\"";
        return args;
    }

    public async Task<(bool Ok, string Message)> UpgradeAsync(UpgradablePackage pkg, CancellationToken ct, Action<string>? onLine = null, bool includeUnknown = false)
    {
        var winget = _findWinget();
        if (winget is null) return (false, "winget not found.");
        var args = BuildUpgradeArguments(pkg, includeUnknown);

        // Several upgrades run at the same time; Windows Installer allows only one MSI at once (error 1618 /
        // 0x80070652 "another installation is already in progress"), so such failures are retried with a pause.
        for (int attempt = 1; ; attempt++)
        {
            var res = await _runCaptured(winget, args, ct, TimeSpan.FromMinutes(30), onLine).ConfigureAwait(false);
            var text = (res.StdOut + "\n" + res.StdErr);
            bool ok = res.ExitCode == 0 || text.Contains("Successfully installed", StringComparison.OrdinalIgnoreCase);
            if (ok) return (true, "Updated.");
            if (IsInstallerBusy(res.ExitCode, text) && attempt < 6)
            {
                onLine?.Invoke($"Another installation is in progress – retrying in 20 s (attempt {attempt}/5)…");
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                continue;
            }
            return (false, DescribeFailure(res.ExitCode, text));
        }
    }

    /// <summary>Human-readable failure: known winget HRESULTs get a name, plus the most telling output line.</summary>
    public static string DescribeFailure(int exitCode, string output)
    {
        var known = DescribeExitCode(exitCode);
        var detail = LastUseful(output);
        var hex = $"0x{unchecked((uint)exitCode):X8}";
        return known != null
            ? $"{known} ({hex})" + (detail.Length > 0 ? " – " + detail : "")
            : $"winget failed ({hex})" + (detail.Length > 0 ? " – " + detail : "");
    }

    /// <summary>The last non-progress line of winget's output (errors come last), trimmed.</summary>
    public static string LastUseful(string s)
    {
        var lines = s.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒' or '▓') && !l.StartsWith("Found ", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var last = lines.LastOrDefault() ?? "";
        return last.Length > 220 ? last[..220] + "…" : last;
    }

    /// <summary>Names for the winget (App Installer) error codes users actually hit. Null for anything else.</summary>
    public static string? DescribeExitCode(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        return code switch
        {
            0x8A150001 => "winget internal error",
            0x8A150002 => "winget rejected the command line",
            0x8A150003 => "the command failed",
            0x8A150006 => "the installer failed to start",
            0x8A150008 => "download failed",
            0x8A15000B => "winget's sources are not configured",
            0x8A150010 => "no installer suits this machine",
            0x8A150011 => "installer hash does not match the manifest (publisher updated the file – try again later)",
            0x8A150014 => "no installed package matches (winget lost track of it)",
            0x8A150016 => "several packages match – ambiguous",
            0x8A150019 => "administrator rights are required for this package",
            0x8A15001B => "Store installs are blocked by policy",
            0x8A15001E => "Microsoft Store install failed",
            0x8A15002B => "no applicable update (the installed version is not upgradeable this way)",
            0x8A15002D => "installer failed the security check",
            0x8A15002E => "downloaded size does not match",
            0x8A15003A => "blocked by policy",
            0x8A150041 => "package agreements were not accepted",
            0x8A150046 => "source agreements were not accepted",
            0x8A150049 => "Windows Installer (MSI) failed",
            0x8A15004F => "the available version is not newer",
            0x8A150050 => "installed version unknown – winget cannot compare versions",
            0x8A150052 => "portable install failed",
            0x8A150056 => "this installer refuses to run elevated – start Evict without administrator rights",
            0x8A150061 => "already installed",
            0x8A150065 => "one or more installs failed",
            0x8A150068 => "package is pinned",
            0x8A150069 => "package is a Store stub",
            0x8A150101 => "the application is in use – close it and retry",
            0x8A150102 => "another installation is in progress",
            0x8A150103 => "a file is in use",
            0x8A150104 => "a dependency is missing",
            0x8A150105 => "disk full",
            0x8A150106 => "insufficient memory",
            0x8A150107 => "no network",
            0x8A150108 => "installer failed – contact the publisher",
            0x8A150109 => "installed – a restart is required to finish",
            0x8A15010A => "a restart is required before installing",
            0x8A15010B => "the installer started a restart",
            0x8A15010C => "cancelled",
            0x8A15010D => "already installed",
            0x8A15010E => "would be a downgrade",
            0x8A15010F => "blocked by policy",
            0x8A150110 => "dependencies could not be installed",
            0x8A150111 => "the application is in use",
            0x8A150112 => "invalid installer parameter",
            0x8A150113 => "not supported on this system",
            0x8A150114 => "upgrade not supported by this installer",
            0x80070005 => "access denied",
            0x80070652 => "another installation is already in progress",
            0x80072EE7 or 0x80072EFD or 0x80072EFE => "network error",
            _ => null,
        };
    }

    /// <summary>MSI "another installation is already in progress" – 1618 as a raw code or wrapped in an HRESULT (0x80070652).</summary>
    public static bool IsInstallerBusy(int exitCode, string output)
        => exitCode == 1618 || exitCode == unchecked((int)0x80070652)
           || output.Contains("0x80070652", StringComparison.OrdinalIgnoreCase)
           || output.Contains("another installation", StringComparison.OrdinalIgnoreCase)
           || output.Contains("installation is already in progress", StringComparison.OrdinalIgnoreCase);
}
