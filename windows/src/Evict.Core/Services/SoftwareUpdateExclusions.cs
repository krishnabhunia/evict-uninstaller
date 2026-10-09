using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>A persistent exclusion follows a package across versions, but never across sources.</summary>
public sealed record SoftwareUpdateExclusion(string Id, string Name, string? Source)
{
    public bool Matches(UpgradablePackage package) => Matches(package.Id, package.Source);
    public bool Matches(string id, string? source) =>
        string.Equals(Id.Trim(), id.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Source?.Trim() ?? "", source?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public static List<SoftwareUpdateExclusion> Add(IEnumerable<SoftwareUpdateExclusion> exclusions,
        IEnumerable<UpgradablePackage> packages)
    {
        var result = exclusions.ToList();
        foreach (var package in packages)
            if (!result.Any(e => e.Matches(package)))
                result.Add(new(package.Id, package.Name, package.Source));
        return result;
    }

    public static IEnumerable<UpgradablePackage> Visible(IEnumerable<UpgradablePackage> packages,
        IEnumerable<SoftwareUpdateExclusion> exclusions) =>
        packages.Where(p => !exclusions.Any(e => e.Matches(p)));
}
