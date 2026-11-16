using Evict.Core.Models;

namespace Evict.Core.Services;

public sealed record SoftwareUpdateScanResult(
    IReadOnlyList<UpgradablePackage> Packages, string? Error, string? Warning, string CheckedSources);

/// <summary>Scans installed managers independently and routes each upgrade to the manager that found it.</summary>
public sealed class SoftwareUpdateSourcesService
{
    private readonly WingetService _winget;
    private readonly ChocolateyService _chocolatey;
    private readonly ScoopService _scoop;

    public SoftwareUpdateSourcesService(WingetService winget, ChocolateyService chocolatey, ScoopService scoop)
    {
        _winget = winget;
        _chocolatey = chocolatey;
        _scoop = scoop;
    }

    public bool IsAvailable => _winget.CanRun || _chocolatey.IsAvailable || _scoop.IsAvailable;

    public async Task<SoftwareUpdateScanResult> GetUpgradesAsync(bool includeUnknown, CancellationToken ct)
    {
        var checks = new List<(string Name, Task<(List<UpgradablePackage> Packages, string? Error)> Task)>();
        if (_winget.CanRun) checks.Add(("WinGet / Microsoft Store", _winget.GetUpgradesAsync(includeUnknown, ct)));
        if (_chocolatey.IsAvailable) checks.Add(("Chocolatey", _chocolatey.GetUpgradesAsync(ct)));
        if (_scoop.IsAvailable) checks.Add(("Scoop", _scoop.GetUpgradesAsync(ct)));
        if (checks.Count == 0)
            return new(Array.Empty<UpgradablePackage>(), "No supported package manager found. Install WinGet, Chocolatey, or Scoop.", null, "None");

        var completed = await Task.WhenAll(checks.Select(async check =>
        {
            try
            {
                var (packages, error) = await check.Task.ConfigureAwait(false);
                return (check.Name, Packages: packages, Error: error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                return (check.Name, Packages: new List<UpgradablePackage>(), Error: ex.Message);
            }
        })).ConfigureAwait(false);

        var errors = completed.Where(c => c.Error != null).Select(c => $"{c.Name}: {c.Error}").ToArray();
        var succeeded = completed.Where(c => c.Error is null).ToArray();
        var packages = succeeded.SelectMany(c => c.Packages)
            .Where(p => SoftwareVersionPolicy.ShouldOffer(p.InstalledVersion, p.AvailableVersion, includeUnknown))
            .GroupBy(p => (Id: p.Id.ToUpperInvariant(), Source: (p.Source ?? "winget").ToUpperInvariant()))
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Source is "chocolatey" or "scoop" ? 0 : 1)
            .ToArray();
        var message = errors.Length == 0 ? null : string.Join("; ", errors);
        return new(packages, succeeded.Length == 0 ? message : null,
            succeeded.Length > 0 ? message : null, string.Join(", ", succeeded.Select(s => s.Name)));
    }

    public Task<WingetUpgradeResult> UpgradeWithResultAsync(UpgradablePackage pkg, CancellationToken ct,
        Action<string>? onLine = null, bool includeUnknown = false)
        => (pkg.Source ?? "winget").ToLowerInvariant() switch
        {
            "winget" or "msstore" => _winget.UpgradeWithResultAsync(pkg, ct, onLine, includeUnknown),
            "chocolatey" => _chocolatey.UpgradeWithResultAsync(pkg, ct, onLine),
            "scoop" => _scoop.UpgradeWithResultAsync(pkg, ct, onLine),
            _ => Task.FromResult(new WingetUpgradeResult(false, $"Unsupported update source: {pkg.Source}")),
        };
}
