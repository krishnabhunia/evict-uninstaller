using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Queries only an installed Chocolatey CLI and executes upgrades through that same manager.</summary>
public sealed class ChocolateyService
{
    private readonly Func<string?> _findChocolatey;
    private readonly Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> _run;

    public ChocolateyService() : this(FindChocolatey, (exe, args, ct, timeout, output) =>
        ProcessRunner.RunCapturedAsync(exe, args, ct, timeout, output)) { }

    internal ChocolateyService(Func<string?> findChocolatey,
        Func<string, string, CancellationToken, TimeSpan, Action<string>?, Task<ProcessResult>> run)
    {
        _findChocolatey = findChocolatey;
        _run = run;
    }

    public bool IsAvailable => _findChocolatey() != null;

    public static string? FindChocolatey()
    {
        var root = Environment.GetEnvironmentVariable("ChocolateyInstall");
        if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");
        var exe = Path.Combine(root, "bin", "choco.exe");
        return File.Exists(exe) ? exe : null;
    }

    public async Task<(List<UpgradablePackage> Packages, string? Error)> GetUpgradesAsync(CancellationToken ct)
    {
        var exe = _findChocolatey();
        if (exe is null) return (new(), "Chocolatey is not installed.");
        var result = await _run(exe, "outdated --limit-output --ignore-unfound --no-color", ct,
            TimeSpan.FromMinutes(4), null).ConfigureAwait(false);
        if (result.TimedOut) return (new(), "Chocolatey did not respond in time.");
        // Chocolatey's optional enhanced exit codes use 2 for 'outdated packages found'.
        if (result.ExitCode is not (0 or 2))
            return (new(), $"Chocolatey check failed ({result.ExitCode}): {WingetService.LastUseful(result.StdErr + "\n" + result.StdOut)}");
        var packages = ParseOutdated(result.StdOut, out var error);
        return (packages, error);
    }

    public static List<UpgradablePackage> ParseOutdated(string output, out string? error)
    {
        error = null;
        var result = new List<UpgradablePackage>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("Chocolatey v", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(line, @"^\d+ packages? outdated\.?$", RegexOptions.IgnoreCase)) continue;
            var fields = line.Split('|');
            if (fields.Length is not (3 or 4) || !IsSafeId(fields[0].Trim())
                || fields[1].Trim().Length == 0 || fields[2].Trim().Length == 0)
            {
                error = "Chocolatey returned an unrecognized outdated-package row; results were discarded.";
                return new();
            }
            var id = fields[0].Trim();
            result.Add(new UpgradablePackage
            {
                Name = id, Id = id, InstalledVersion = fields[1].Trim(),
                AvailableVersion = fields[2].Trim(), Source = "chocolatey",
            });
        }
        return result;
    }

    internal static bool IsSafeId(string id) => Regex.IsMatch(id, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$");

    public async Task<WingetUpgradeResult> UpgradeWithResultAsync(UpgradablePackage pkg, CancellationToken ct,
        Action<string>? onLine = null)
    {
        if (!IsSafeId(pkg.Id)) return new(false, "Invalid Chocolatey package identifier.");
        var exe = _findChocolatey();
        if (exe is null) return new(false, "Chocolatey is not installed.");
        var result = await _run(exe, $"upgrade \"{pkg.Id}\" --yes --no-progress --no-color", ct,
            TimeSpan.FromMinutes(30), onLine).ConfigureAwait(false);
        if (result.TimedOut) return new(false, "Chocolatey timed out; check the installed version before retrying.", false);
        if (result.ExitCode is 3010 or 1641) return new(true, WingetService.RestartToFinishMessage);
        if (result.ExitCode == 0) return new(true, "Updated.");
        return new(false, $"Chocolatey failed ({result.ExitCode}): {WingetService.LastUseful(result.StdErr + "\n" + result.StdOut)}");
    }
}
