using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Evict.Core.Services;

/// <summary>
/// What the Info window says about this copy (design IN1): version, channel, the pull request and commit it was
/// made from, when it was built, its GitHub tag and how it is installed. CI passes the build details as MSBuild
/// properties (SourcePr, SourceCommit, BuildTimeUtc, ReleaseTag → AssemblyMetadata); a local build has none.
/// </summary>
public sealed record BuildInfo(
    string Version,
    string? SourcePr,
    string? SourceCommit,
    DateTimeOffset? BuiltUtc,
    string? ReleaseTag,
    string InstalledAs)
{
    public const string LocalBuild = "Local build";
    public const string AppName = "Evict";

    private static readonly Regex ShortBeta = new(@"-beta(?<n>[0-9]{3})\z", RegexOptions.CultureInvariant);

    /// <summary>Reads the CI metadata stamped into <paramref name="assembly"/> (empty values when built locally).</summary>
    public static BuildInfo FromAssembly(Assembly assembly, string version, string installedAs)
    {
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(a => a.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        string? Get(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return Create(version, Get("SourcePr"), Get("SourceCommit"), Get("BuildTimeUtc"), Get("ReleaseTag"), installedAs);
    }

    /// <summary>Validates raw metadata: anything malformed is treated as missing rather than shown.</summary>
    public static BuildInfo Create(string version, string? pr, string? commit, string? builtUtc, string? tag, string installedAs)
    {
        pr = pr is not null && Regex.IsMatch(pr, @"\A[1-9][0-9]{0,8}\z") ? pr : null;
        commit = commit is not null && Regex.IsMatch(commit, @"\A[0-9a-fA-F]{7,40}\z") ? commit.ToLowerInvariant() : null;
        tag = tag is not null && UpdateVersion.Parse(tag) is not null && tag.StartsWith("win-v", StringComparison.Ordinal) ? tag : null;
        DateTimeOffset? built = DateTimeOffset.TryParse(builtUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
        return new(version, pr, commit, built, tag, installedAs);
    }

    public UpdateVersion? Semantic => UpdateVersion.Parse(Version);
    public bool IsBeta => Semantic?.IsPrerelease == true;
    public string Channel => IsBeta ? "Beta" : "Stable";
    /// <summary>x.y.z without the beta suffix.</summary>
    public string CoreVersion => Semantic is { } v ? UpdateChecker.Normalize(v.Core).ToString(3) : Version;
    /// <summary>"007" for 4.2.0-beta007; null for stable, old long-format and unreserved (000) builds.</summary>
    public string? BetaNumber => ShortBeta.Match(Version) is { Success: true } m && m.Groups["n"].Value != "000" ? m.Groups["n"].Value : null;
    public bool IsLocalBuild => SourceCommit is null && BuiltUtc is null;

    public string Subtitle(string platform = "Windows") => $"Version {Version} · {Channel} · {platform}";

    /// <summary>The banner on the This build tab; null for a stable copy.</summary>
    public string? BetaBanner => !IsBeta ? null
        : BetaNumber is { } n
            ? $"This is a test build. It is beta {n} of {AppName} {CoreVersion}, made from a pull request."
            : $"This is a test build of {AppName} {CoreVersion}, made from a pull request.";

    public string? PullRequestUrl => SourcePr is null ? null : $"{UpdateChecker.RepoUrl}/pull/{SourcePr}";
    public string MadeFromText => SourcePr is null ? (IsLocalBuild ? LocalBuild : "main branch") : $"PR #{SourcePr}";
    public string CommitText => SourceCommit is null ? LocalBuild : SourceCommit[..Math.Min(7, SourceCommit.Length)];
    public string TagText => ReleaseTag ?? (IsLocalBuild ? LocalBuild : "Not published");
    public string DownloadName => $"{AppName}_{Version}.zip";

    /// <summary>"04-Nov-2026, 19:55" in the PC's time zone.</summary>
    public string BuiltOnText(TimeZoneInfo? zone = null) => BuiltUtc is not { } built ? LocalBuild
        : TimeZoneInfo.ConvertTime(built, zone ?? TimeZoneInfo.Local).ToString("dd-MMM-yyyy, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The table on the This build tab, in display order.</summary>
    public IReadOnlyList<(string Item, string Value)> Rows(TimeZoneInfo? zone = null)
    {
        var rows = new List<(string, string)> { ("Version", Version), ("Channel", Channel) };
        if (SourcePr is not null) rows.Add(("Made from", MadeFromText));
        rows.Add(("Commit", CommitText));
        rows.Add(("Built on", BuiltOnText(zone)));
        rows.Add(("GitHub tag", TagText));
        rows.Add(("Download", DownloadName));
        rows.Add(("Installed as", InstalledAs));
        return rows;
    }

    /// <summary>
    /// Two lines for bug reports, e.g.
    /// "Evict 4.2.0-beta001 (Beta) · PR #35 · 4e68c2a · built 2026-11-04 14:25 UTC" and
    /// "Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8".
    /// </summary>
    public string CopyText(string operatingSystem, string runtime)
    {
        var first = new List<string> { $"{AppName} {Version} ({Channel})" };
        if (SourcePr is not null) first.Add($"PR #{SourcePr}");
        first.Add(SourceCommit is null ? LocalBuild.ToLowerInvariant() : CommitText);
        if (BuiltUtc is { } built) first.Add("built " + built.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
        return string.Join(" · ", first) + Environment.NewLine + string.Join(" · ", operatingSystem, InstalledAs, runtime);
    }

    /// <summary>How this copy runs: the Setup marker for the current user, for all users, or neither (portable).</summary>
    public static string DescribeInstall(string? perUserInstallDir, string? allUsersInstallDir, string exeDirectory)
    {
        if (UpdateChecker.SameDirectory(perUserInstallDir, exeDirectory)) return "installer, per-user";
        if (UpdateChecker.SameDirectory(allUsersInstallDir, exeDirectory)) return "installer, all users";
        return "portable";
    }

    /// <summary>"Windows 11 Pro 24H2 x64" from the registry's product name, display version and build.</summary>
    public static string DescribeWindows(string? productName, string? displayVersion, int build, bool is64Bit)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName.Trim();
        // Windows 11 still reports "Windows 10" as its product name; build 22000 and later is Windows 11.
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name["Windows 10".Length..];
        var parts = new List<string> { name };
        if (!string.IsNullOrWhiteSpace(displayVersion)) parts.Add(displayVersion.Trim());
        parts.Add(is64Bit ? "x64" : "x86");
        return string.Join(" ", parts);
    }

    /// <summary>".NET 8" from the running runtime version.</summary>
    public static string DescribeRuntime(Version runtime) => $".NET {runtime.Major}";
}
