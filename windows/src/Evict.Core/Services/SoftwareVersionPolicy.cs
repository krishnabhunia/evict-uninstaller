using System.Globalization;
using System.Text.RegularExpressions;

namespace Evict.Core.Services;

/// <summary>
/// Update eligibility uses major.minor.patch only. Windows registry versions and package feeds often
/// append a fourth (or fifth) build component, which must not trigger an update by itself.
/// </summary>
public static class SoftwareVersionPolicy
{
    private static readonly Regex NumericVersion = new(
        @"^\s*[vV]?(?<major>\d+)(?:\.(?<minor>\d+))?(?:\.(?<patch>\d+))?(?:\.\d+)*(?:[-+_][\w.+-]+|\s+\([^)]*\))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ShouldOffer(string? installed, string? available, bool includeUnknown)
    {
        if (string.IsNullOrWhiteSpace(available)) return false;
        if (!TryParseThree(available, out var latest)) return false;
        if (!TryParseThree(installed, out var current)) return includeUnknown;
        return latest.CompareTo(current) > 0;
    }

    private static bool TryParseThree(string? value, out (long Major, long Minor, long Patch) parts)
    {
        parts = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = NumericVersion.Match(value);
        if (!match.Success
            || !long.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !TryPart(match.Groups["minor"], out var minor)
            || !TryPart(match.Groups["patch"], out var patch)) return false;
        parts = (major, minor, patch);
        return true;
    }

    private static bool TryPart(Group group, out long value)
    {
        value = 0;
        return !group.Success || long.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
