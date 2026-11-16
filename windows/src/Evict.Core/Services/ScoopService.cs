using System.Text;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Checks an installed Scoop repository without updating buckets or modifying local manifests.</summary>
public sealed class ScoopService
{
    private readonly Func<string?> _findScoop;
    private readonly Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> _run;

    public ScoopService() : this(FindScoop, (exe, args, ct, timeout, output) =>
        ProcessRunner.RunCapturedAsync(exe, args, ct, timeout, output)) { }

    internal ScoopService(Func<string?> findScoop,
        Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> run)
    {
        _findScoop = findScoop;
        _run = run;
    }

    public bool IsAvailable => _findScoop() != null;

    public static string? FindScoop()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("SCOOP"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop"),
            Environment.GetEnvironmentVariable("SCOOP_GLOBAL"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop"),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var script = Path.Combine(root!, "apps", "scoop", "current", "bin", "scoop.ps1");
            if (File.Exists(script)) return script;
        }
        return null;
    }

    /// <summary>Uses PowerShell's encoded-command form so neither paths nor package names are interpreted by cmd.exe.</summary>
    internal static string BuildPowerShellArguments(string script, string command, string? id = null)
    {
        if (command is not ("status" or "update") || (id != null && !ChocolateyService.IsSafeId(id)))
            throw new ArgumentException("Invalid Scoop command or package identifier.");
        var invocation = $"$ErrorActionPreference='Stop'; & '{script.Replace("'", "''")}' {command}"
            + (id is null ? "" : $" '{id}'")
            + (command == "status" ? " | Out-String -Width 4096" : "");
        return "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(invocation));
    }

    public async Task<(List<UpgradablePackage> Packages, string? Error)> GetUpgradesAsync(CancellationToken ct)
    {
        var script = _findScoop();
        if (script is null) return (new(), "Scoop is not installed.");
        var res = await _run("powershell.exe", BuildPowerShellArguments(script, "status"), ct,
            TimeSpan.FromMinutes(4), null).ConfigureAwait(false);
        if (res.TimedOut) return (new(), "Scoop did not respond in time.");
        if (!res.Success) return (new(), $"Scoop check failed ({res.ExitCode}): {WingetService.LastUseful(res.StdErr + "\n" + res.StdOut)}");
        return (ParseStatus(res.StdOut, out var error), error);
    }

    public static List<UpgradablePackage> ParseStatus(string output, out string? error)
    {
        error = null;
        var packages = new List<UpgradablePackage>();
        var clean = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        var lines = clean.Replace("\r", "").Split('\n');
        var headerIndex = Array.FindIndex(lines, l => l.Contains("Installed Version", StringComparison.Ordinal)
            && l.Contains("Latest Version", StringComparison.Ordinal));
        if (headerIndex < 0)
        {
            if (clean.Contains("Scoop is up to date", StringComparison.OrdinalIgnoreCase)
                || clean.Contains("Everything is up to date", StringComparison.OrdinalIgnoreCase)) return packages;
            error = "Scoop returned an unrecognized status; updates could not be checked safely.";
            return new();
        }
        var header = lines[headerIndex];
        int installedStart = header.IndexOf("Installed Version", StringComparison.Ordinal);
        int latestStart = header.IndexOf("Latest Version", StringComparison.Ordinal);
        int afterLatest = header.IndexOf("Missing Dependencies", latestStart, StringComparison.Ordinal);
        if (afterLatest < 0) afterLatest = header.IndexOf("Info", latestStart, StringComparison.Ordinal);
        if (afterLatest < 0) afterLatest = int.MaxValue;

        if (headerIndex + 1 >= lines.Length || !Regex.IsMatch(lines[headerIndex + 1], @"^\s*-{2,}\s+-{2,}\s+-{2,}"))
        {
            error = "Scoop status had an invalid table separator.";
            return new();
        }
        for (int i = headerIndex + 2; i < lines.Length; i++)
        {
            var row = lines[i];
            if (string.IsNullOrWhiteSpace(row)) continue;
            if (row.Length <= latestStart)
            {
                error = "Scoop returned an incomplete package row; results were discarded.";
                return new();
            }
            var name = row[..Math.Min(installedStart, row.Length)].Trim();
            var installed = row[installedStart..Math.Min(latestStart, row.Length)].Trim();
            var latest = row[latestStart..Math.Min(afterLatest, row.Length)].Trim();
            if (!ChocolateyService.IsSafeId(name) || installed.Length == 0 || latest.Length == 0)
            {
                error = "Scoop returned an invalid package row; results were discarded.";
                return new();
            }
            // Held applications intentionally opt out of Scoop upgrades.
            var info = afterLatest < row.Length ? row[afterLatest..] : "";
            if (info.Contains("hold", StringComparison.OrdinalIgnoreCase)) continue;
            packages.Add(new UpgradablePackage
            {
                Name = name, Id = name, InstalledVersion = installed, AvailableVersion = latest, Source = "scoop",
            });
        }
        return packages;
    }

    public async Task<WingetUpgradeResult> UpgradeWithResultAsync(UpgradablePackage pkg, CancellationToken ct,
        Action<string>? onLine = null)
    {
        if (!ChocolateyService.IsSafeId(pkg.Id)) return new(false, "Invalid Scoop package identifier.");
        var script = _findScoop();
        if (script is null) return new(false, "Scoop is not installed.");
        var res = await _run("powershell.exe", BuildPowerShellArguments(script, "update", pkg.Id), ct,
            TimeSpan.FromMinutes(30), onLine).ConfigureAwait(false);
        if (res.TimedOut) return new(false, "Scoop timed out; check the installed version before retrying.", false);
        return res.Success ? new(true, "Updated.")
            : new(false, $"Scoop failed ({res.ExitCode}): {WingetService.LastUseful(res.StdErr + "\n" + res.StdOut)}");
    }
}
