using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Why an update winget offers is left out of the Software Updater list and the Health count (design VM1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HiddenUpdateReason
{
    /// <summary>An update ran, yet winget still offers the same versions afterwards (rule 1).</summary>
    AlreadyUpToDate,
    /// <summary>Installed and available are the same version written differently, e.g. 1.2 and 1.2.0.0 (rule 2).</summary>
    SameVersion,
    /// <summary>The available version is the installed one plus one extra number, e.g. 6.43.18 → 6.43.18.1 (rule 3).</summary>
    Repackaged,
    /// <summary>The user skipped this one available version (rule 4).</summary>
    SkippedVersion,
    /// <summary>The user excluded the package for every version (rule 5, existing exclusions).</summary>
    Excluded,
}

/// <summary>What a saved <see cref="HiddenUpdateRecord"/> says about one exact offer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HiddenUpdateRecordKind
{
    /// <summary>The offer survived an update unchanged: treat it as already up to date.</summary>
    AlreadyUpToDate,
    /// <summary>The user skipped this available version.</summary>
    SkippedVersion,
    /// <summary>The user restored an automatically hidden offer: list it even though a rule would hide it.</summary>
    ShowAnyway,
}

/// <summary>
/// One remembered decision about an offer. A record applies only while winget offers exactly the same versions
/// (skipped versions: the same available version), so it stops applying by itself when the offer changes.
/// </summary>
public sealed record HiddenUpdateRecord(string Id, string Name, string? Source, string? InstalledVersion,
    string? AvailableVersion, HiddenUpdateRecordKind Kind, DateTime SinceUtc)
{
    public bool SamePackage(UpgradablePackage package) =>
        string.Equals(Id.Trim(), package.Id.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Source?.Trim() ?? "", package.Source?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    public bool SameOffer(UpgradablePackage package) => SamePackage(package)
        && SoftwareVersionMatch.SameText(InstalledVersion, package.InstalledVersion)
        && SoftwareVersionMatch.SameText(AvailableVersion, package.AvailableVersion);

    /// <summary>True while this record still describes what winget offers for the package.</summary>
    public bool Applies(UpgradablePackage package) => Kind == HiddenUpdateRecordKind.SkippedVersion
        ? SamePackage(package) && SoftwareVersionMatch.SameText(AvailableVersion, package.AvailableVersion)
        : SameOffer(package);
}

/// <summary>Version comparisons that work for any package, not only ones Evict knows.</summary>
public static class SoftwareVersionMatch
{
    private static readonly Regex NumericPart = new(@"^\d{1,9}$", RegexOptions.CultureInvariant);

    public static bool SameText(string? left, string? right) =>
        string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dotted numbers only ("v" prefix and spaces allowed); null for anything else ("Unknown", "&lt; 1.0", "1.2-beta").</summary>
    internal static int[]? Numeric(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var text = version.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..].TrimStart();
        var parts = text.Split('.');
        if (parts.Any(p => !NumericPart.IsMatch(p))) return null;
        return parts.Select(int.Parse).ToArray();
    }

    private static int[] WithoutTrailingZeros(int[] parts)
    {
        int length = parts.Length;
        while (length > 1 && parts[length - 1] == 0) length--;
        return parts[..length];
    }

    /// <summary>Rule 2: "1.2" vs "1.2.0.0", "v5.1" vs "5.1" – the same release written differently.</summary>
    public static bool IsSameVersion(string? installed, string? available)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(available)) return false;
        if (SameText(installed, available)) return true;
        var a = Numeric(installed);
        var b = Numeric(available);
        return a != null && b != null && WithoutTrailingZeros(a).SequenceEqual(WithoutTrailingZeros(b));
    }

    /// <summary>
    /// Rule 3: the available version is the installed version (at least three numbers) plus exactly one extra non-zero
    /// number – winget's revision for a re-uploaded installer of the same build (6.43.18 → 6.43.18.1).
    /// "6.43 → 6.43.1" (a patch release) and "1.2.3.0 → 1.2.3.1" (same length) are real updates and stay listed.
    /// </summary>
    public static bool IsRepackage(string? installed, string? available)
    {
        var a = Numeric(installed);
        var b = Numeric(available);
        if (a is null || b is null || a.Length < 3 || b.Length != a.Length + 1) return false;
        return b[..a.Length].SequenceEqual(a) && b[^1] > 0;
    }
}

public sealed record SoftwareUpdateHidingOptions(bool HideUnchanged = true, bool HideRepackaged = true)
{
    public static SoftwareUpdateHidingOptions From(AppSettings settings) =>
        new(settings.HideUnchangedSoftwareUpdates, settings.HideRepackagedSoftwareUpdates);
}

/// <summary>An offered update that is not listed, and why.</summary>
public sealed record HiddenUpdate(UpgradablePackage Package, HiddenUpdateReason Reason, DateTime? SinceUtc)
{
    public bool IsAutomatic => Reason is HiddenUpdateReason.AlreadyUpToDate or HiddenUpdateReason.SameVersion or HiddenUpdateReason.Repackaged;
}

public sealed record SoftwareUpdateView(IReadOnlyList<UpgradablePackage> Visible, IReadOnlyList<HiddenUpdate> Hidden);

/// <summary>
/// The one filter every update count goes through (Software Updater list, Health tile, tray and scheduled-scan
/// summaries). Rules are generic: they look only at package identity and version numbers.
/// </summary>
public static class SoftwareUpdateFilter
{
    public static SoftwareUpdateView Classify(IEnumerable<UpgradablePackage> packages,
        IEnumerable<SoftwareUpdateExclusion>? exclusions, IEnumerable<HiddenUpdateRecord>? records,
        SoftwareUpdateHidingOptions options)
    {
        var excluded = (exclusions ?? Array.Empty<SoftwareUpdateExclusion>()).ToList();
        var saved = (records ?? Array.Empty<HiddenUpdateRecord>()).ToList();
        var visible = new List<UpgradablePackage>();
        var hidden = new List<HiddenUpdate>();
        foreach (var package in packages)
        {
            if (excluded.Any(e => e.Matches(package)))
            {
                hidden.Add(new(package, HiddenUpdateReason.Excluded, null));
                continue;
            }
            var skipped = saved.FirstOrDefault(r => r.Kind == HiddenUpdateRecordKind.SkippedVersion && r.Applies(package));
            if (skipped != null)
            {
                hidden.Add(new(package, HiddenUpdateReason.SkippedVersion, skipped.SinceUtc));
                continue;
            }
            if (saved.Any(r => r.Kind == HiddenUpdateRecordKind.ShowAnyway && r.Applies(package)))
            {
                visible.Add(package);
                continue;
            }
            var stuck = options.HideUnchanged
                ? saved.FirstOrDefault(r => r.Kind == HiddenUpdateRecordKind.AlreadyUpToDate && r.Applies(package)) : null;
            if (stuck != null)
                hidden.Add(new(package, HiddenUpdateReason.AlreadyUpToDate, stuck.SinceUtc));
            else if (options.HideUnchanged && SoftwareVersionMatch.IsSameVersion(package.InstalledVersion, package.AvailableVersion))
                hidden.Add(new(package, HiddenUpdateReason.SameVersion, null));
            else if (options.HideRepackaged && SoftwareVersionMatch.IsRepackage(package.InstalledVersion, package.AvailableVersion))
                hidden.Add(new(package, HiddenUpdateReason.Repackaged, null));
            else
                visible.Add(package);
        }
        return new(visible, hidden);
    }

    /// <summary>
    /// Drops records whose package winget now offers with other versions: the offer changed, so the decision no longer
    /// applies. Records for packages missing from this check are kept (a check without "unknown versions" omits some).
    /// </summary>
    public static List<HiddenUpdateRecord> Prune(IEnumerable<HiddenUpdateRecord> records, IEnumerable<UpgradablePackage> packages)
    {
        var offered = packages.ToList();
        return records.Where(r =>
        {
            var current = offered.Where(r.SamePackage).ToList();
            return current.Count == 0 || current.Any(r.Applies);
        }).ToList();
    }

    /// <summary>Rule 1: attempted updates that winget still offers with exactly the same versions after a fresh check.</summary>
    public static List<UpgradablePackage> FindUnchanged(IEnumerable<UpgradablePackage> attempted, IEnumerable<UpgradablePackage> afterCheck)
    {
        var after = afterCheck.ToList();
        return attempted.Where(a => after.Any(p => SameOffer(a, p))).ToList();
    }

    public static List<HiddenUpdateRecord> MarkAlreadyUpToDate(IEnumerable<HiddenUpdateRecord> records,
        IEnumerable<UpgradablePackage> packages, DateTime nowUtc)
    {
        var result = records.ToList();
        foreach (var package in packages)
        {
            result.RemoveAll(r => r.Kind is HiddenUpdateRecordKind.AlreadyUpToDate or HiddenUpdateRecordKind.ShowAnyway && r.SameOffer(package));
            result.Add(Record(package, HiddenUpdateRecordKind.AlreadyUpToDate, nowUtc));
        }
        return result;
    }

    /// <summary>Rule 4: hide these available versions only; a newer one is listed again.</summary>
    public static List<HiddenUpdateRecord> SkipVersions(IEnumerable<HiddenUpdateRecord> records,
        IEnumerable<UpgradablePackage> packages, DateTime nowUtc)
    {
        var result = records.ToList();
        foreach (var package in packages)
        {
            result.RemoveAll(r => r.Kind == HiddenUpdateRecordKind.SkippedVersion && r.SamePackage(package));
            result.Add(Record(package, HiddenUpdateRecordKind.SkippedVersion, nowUtc));
        }
        return result;
    }

    /// <summary>
    /// Lists a hidden offer again. Skipped versions lose their record; automatically hidden offers get a
    /// "show anyway" record for exactly these versions. Exclusions are restored through <see cref="SoftwareUpdateExclusion"/>.
    /// </summary>
    public static List<HiddenUpdateRecord> Restore(IEnumerable<HiddenUpdateRecord> records,
        IEnumerable<HiddenUpdate> hidden, DateTime nowUtc)
    {
        var result = records.ToList();
        foreach (var item in hidden)
        {
            switch (item.Reason)
            {
                case HiddenUpdateReason.SkippedVersion:
                    result.RemoveAll(r => r.Kind == HiddenUpdateRecordKind.SkippedVersion && r.SamePackage(item.Package));
                    break;
                case HiddenUpdateReason.AlreadyUpToDate:
                case HiddenUpdateReason.SameVersion:
                case HiddenUpdateReason.Repackaged:
                    result.RemoveAll(r => r.Kind is HiddenUpdateRecordKind.AlreadyUpToDate or HiddenUpdateRecordKind.ShowAnyway && r.SameOffer(item.Package));
                    result.Add(Record(item.Package, HiddenUpdateRecordKind.ShowAnyway, nowUtc));
                    break;
            }
        }
        return result;
    }

    public static string ReasonText(HiddenUpdateReason reason) => reason switch
    {
        HiddenUpdateReason.AlreadyUpToDate => "Already up to date · auto",
        HiddenUpdateReason.SameVersion => "Same version · auto",
        HiddenUpdateReason.Repackaged => "Same build, repackaged · auto",
        HiddenUpdateReason.SkippedVersion => "Version skipped · by you",
        _ => "Excluded · by you",
    };

    public static string ReasonDetail(HiddenUpdate hidden)
    {
        var p = hidden.Package;
        return hidden.Reason switch
        {
            HiddenUpdateReason.AlreadyUpToDate => $"Updated{When(hidden.SinceUtc)}, but Windows still reports {p.InstalledVersion}.",
            HiddenUpdateReason.SameVersion => $"{p.InstalledVersion} and {p.AvailableVersion} are the same version written differently.",
            HiddenUpdateReason.Repackaged => $"{p.AvailableVersion} only adds a package revision to the installed build {p.InstalledVersion}.",
            HiddenUpdateReason.SkippedVersion => $"Skipped{When(hidden.SinceUtc)}.",
            _ => "Never offered until restored.",
        };
    }

    public static string ComesBackText(HiddenUpdateReason reason) => reason switch
    {
        HiddenUpdateReason.AlreadyUpToDate => "winget offers another version, or the installed version changes",
        HiddenUpdateReason.SameVersion => "The two versions stop being equal",
        HiddenUpdateReason.Repackaged => "A version that differs in an earlier number appears",
        HiddenUpdateReason.SkippedVersion => "A newer version than the skipped one appears",
        _ => "Only when you restore it",
    };

    /// <summary>"1 already up to date · 1 version you skipped · 2 excluded programs" for the banner above the list.</summary>
    public static string? Summary(IReadOnlyCollection<HiddenUpdate> hidden, int excludedNotOffered = 0)
    {
        int auto = hidden.Count(h => h.IsAutomatic);
        int skipped = hidden.Count(h => h.Reason == HiddenUpdateReason.SkippedVersion);
        int excluded = hidden.Count(h => h.Reason == HiddenUpdateReason.Excluded) + excludedNotOffered;
        var parts = new List<string>();
        if (auto > 0) parts.Add($"{auto} already up to date (version label mismatch)");
        if (skipped > 0) parts.Add(skipped == 1 ? "1 version you skipped" : $"{skipped} versions you skipped");
        if (excluded > 0) parts.Add(excluded == 1 ? "1 excluded program" : $"{excluded} excluded programs");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string When(DateTime? utc) =>
        utc is { } d ? " " + d.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";

    private static bool SameOffer(UpgradablePackage a, UpgradablePackage b) =>
        string.Equals(a.Id.Trim(), b.Id.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Source?.Trim() ?? "", b.Source?.Trim() ?? "", StringComparison.OrdinalIgnoreCase)
        && SoftwareVersionMatch.SameText(a.InstalledVersion, b.InstalledVersion)
        && SoftwareVersionMatch.SameText(a.AvailableVersion, b.AvailableVersion);

    private static HiddenUpdateRecord Record(UpgradablePackage p, HiddenUpdateRecordKind kind, DateTime nowUtc) =>
        new(p.Id.Trim(), p.Name, string.IsNullOrWhiteSpace(p.Source) ? null : p.Source.Trim(),
            p.InstalledVersion?.Trim(), p.AvailableVersion?.Trim(), kind, nowUtc);
}
