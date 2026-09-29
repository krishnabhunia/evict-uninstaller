using System.Text;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>
/// Software Updater backed by the Windows Package Manager (winget). Lists upgradable packages by
/// parsing the fixed-width table "winget upgrade" prints, and upgrades them one at a time.
/// </summary>
public sealed class WingetService
{
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
        var winget = FindWinget();
        if (winget is null) return (new(), "winget (App Installer) was not found. Install it from the Microsoft Store to use Software Updater.");
        var args = "upgrade --accept-source-agreements --disable-interactivity" + (includeUnknown ? " --include-unknown" : "");
        var res = await ProcessRunner.RunCapturedAsync(winget, args, ct, TimeSpan.FromMinutes(4), onLine, Encoding.UTF8).ConfigureAwait(false);
        if (res.TimedOut) return (new(), "winget did not respond in time.");
        var list = ParseUpgradeTable(res.StdOut);
        string? err = null;
        if (list.Count == 0 && res.ExitCode != 0 && !res.StdOut.Contains("No installed package", StringComparison.OrdinalIgnoreCase))
            err = DescribeFailure(res.ExitCode, res.StdOut + res.StdErr);
        return (list, err);
    }

    private static string FirstUseful(string s) =>
        s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.All(c => c is '-' or '\\' or '|' or '/' or ' ')) ?? "";

    /// <summary>
    /// Parses winget's table. Columns are located from the header line so localisation and
    /// column width changes do not break us: "Name  Id  Version  Available  Source".
    /// </summary>
    public static List<UpgradablePackage> ParseUpgradeTable(string output)
    {
        var result = new List<UpgradablePackage>();
        if (string.IsNullOrWhiteSpace(output)) return result;
        var lines = output.Replace("\r", "").Split('\n');

        int headerIdx = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Contains(" Id ") && l.Contains("Version") && (l.Contains("Available") || l.Contains("Verfügbar") || l.Contains("Disponible")))
            {
                headerIdx = i; break;
            }
        }
        if (headerIdx < 0 || headerIdx + 1 >= lines.Length) return result;

        var header = lines[headerIdx];
        int idCol = header.IndexOf(" Id ", StringComparison.Ordinal) + 1;
        int verCol = header.IndexOf("Version", idCol, StringComparison.Ordinal);
        int availCol = IndexOfAny(header, verCol + 7, "Available", "Verfügbar", "Disponible");
        int srcCol = IndexOfAny(header, availCol + 5, "Source", "Quelle", "Fuente", "Origine");
        if (idCol <= 0 || verCol <= idCol || availCol <= verCol) return result;

        for (int i = headerIdx + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (line.TrimStart().StartsWith("---") || line.Trim().All(c => c == '-')) continue;   // separator
            if (line.Contains("upgrades available", StringComparison.OrdinalIgnoreCase) || line.Contains("upgrade available", StringComparison.OrdinalIgnoreCase)) break;
            if (line.StartsWith("The following packages", StringComparison.OrdinalIgnoreCase)) break; // pinned / unknown section
            if (line.Length < verCol) continue;

            string name = Slice(line, 0, idCol);
            string id = Slice(line, idCol, verCol);
            string version = Slice(line, verCol, availCol);
            string available = srcCol > availCol ? Slice(line, availCol, srcCol) : Slice(line, availCol, line.Length);
            string source = srcCol > availCol ? Slice(line, srcCol, line.Length) : "";

            if (id.Length == 0 || name.Length == 0) continue;
            if (id.Contains(' ') && !id.Contains('.')) continue; // misaligned wide-character row; skip rather than mis-parse
            result.Add(new UpgradablePackage { Name = name, Id = id, InstalledVersion = version, AvailableVersion = available, Source = source });
        }
        return result;
    }

    private static int IndexOfAny(string s, int start, params string[] needles)
    {
        if (start < 0 || start >= s.Length) return -1;
        foreach (var n in needles)
        {
            int i = s.IndexOf(n, start, StringComparison.Ordinal);
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length || start < 0) return "";
        end = Math.Min(end, line.Length);
        if (end <= start) return "";
        return line[start..end].Trim().TrimEnd('…');
    }

    public async Task<(bool Ok, string Message)> UpgradeAsync(UpgradablePackage pkg, CancellationToken ct, Action<string>? onLine = null)
    {
        var winget = FindWinget();
        if (winget is null) return (false, "winget not found.");
        var args = $"upgrade --id \"{pkg.Id}\" --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
        if (!string.IsNullOrEmpty(pkg.Source)) args += $" --source {pkg.Source}";

        // Several upgrades run at the same time; Windows Installer allows only one MSI at once (error 1618 /
        // 0x80070652 "another installation is already in progress"), so such failures are retried with a pause.
        for (int attempt = 1; ; attempt++)
        {
            var res = await ProcessRunner.RunCapturedAsync(winget, args, ct, TimeSpan.FromMinutes(30), onLine, Encoding.UTF8).ConfigureAwait(false);
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
