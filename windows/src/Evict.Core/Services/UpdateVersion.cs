using System.Text.RegularExpressions;

namespace Evict.Core.Services;

/// <summary>Release precedence, including ordered prerelease identifiers. Build metadata is ignored.</summary>
public sealed record UpdateVersion(Version Core, string? Prerelease = null) : IComparable<UpdateVersion>
{
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    public static UpdateVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Short legacy versions are normalized; release identities otherwise follow SemVer.
        var match = Regex.Match(text.Trim(),
            @"^(?:win-v|v|release-)?(?<major>0|[1-9][0-9]*)(?:\.(?<minor>0|[1-9][0-9]*))?(?:\.(?<patch>0|[1-9][0-9]*))?(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["major"].Value, out var major)) return null;
        int minor = 0, patch = 0;
        if (match.Groups["minor"].Success && !int.TryParse(match.Groups["minor"].Value, out minor)) return null;
        if (match.Groups["patch"].Success && !int.TryParse(match.Groups["patch"].Value, out patch)) return null;
        string? prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;
        if (prerelease != null && !match.Groups["patch"].Success) return null;
        if (prerelease != null && prerelease.Split('.').Any(id => IsNumeric(id) && id.Length > 1 && id[0] == '0')) return null;
        return new(new Version(major, minor, patch), prerelease);
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        int core = UpdateChecker.Normalize(Core).CompareTo(UpdateChecker.Normalize(other.Core));
        if (core != 0) return core;
        if (!IsPrerelease) return other.IsPrerelease ? 1 : 0;
        if (!other.IsPrerelease) return -1;
        var left = Prerelease!.Split('.');
        var right = other.Prerelease!.Split('.');
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            bool leftNumeric = IsNumeric(left[i]), rightNumeric = IsNumeric(right[i]);
            int comparison;
            if (leftNumeric && rightNumeric)
            {
                // Length and ordinal comparison avoid overflow for arbitrarily long numeric identifiers.
                comparison = left[i].Length.CompareTo(right[i].Length);
                if (comparison == 0) comparison = string.CompareOrdinal(left[i], right[i]);
            }
            else if (leftNumeric != rightNumeric) comparison = leftNumeric ? -1 : 1;
            else comparison = string.CompareOrdinal(left[i], right[i]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool IsNumeric(string identifier) => identifier.All(c => c is >= '0' and <= '9');

    public override string ToString() => UpdateChecker.Normalize(Core).ToString(3) + (IsPrerelease ? "-" + Prerelease : "");
}
