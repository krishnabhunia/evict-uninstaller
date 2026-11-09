using System.Reflection;
using System.Text.RegularExpressions;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Short beta versions (BT1 A) and the Info window's build details, feature list and changelog (IN1).</summary>
public sealed class ShortBetaVersionTests
{
    [Theory]
    [InlineData("2.16.0-beta001", "2.16.0-beta.18.29", 1)]     // a new short beta outranks an old long beta of the same core
    [InlineData("2.16.0-beta001", "2.16.0-beta.99.99.9", 1)]
    [InlineData("15.0.0-beta007", "15.0.0", -1)]               // and stays below its stable release
    [InlineData("15.0.0-beta009", "15.0.0-beta010", -1)]
    [InlineData("15.0.0-beta099", "15.0.0-beta100", -1)]
    [InlineData("15.0.0-beta999", "15.0.1-beta001", -1)]
    [InlineData("15.0.0-beta007", "14.9.9", 1)]
    [InlineData("15.0.0-beta007", "15.0.0-beta007", 0)]
    public void ShortBetaPrecedence(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(UpdateVersion.Parse(left)!.CompareTo(UpdateVersion.Parse(right))));

    [Theory]
    [InlineData("win-v15.0.0-beta007")]
    [InlineData("v15.0.0-beta007")]
    [InlineData("15.0.0-beta000")]
    public void ShortBetaParsesAsOneAlphanumericIdentifier(string value)
    {
        var version = UpdateVersion.Parse(value)!;
        Assert.True(version.IsPrerelease);
        Assert.Matches(@"\Abeta[0-9]{3}\z", version.Prerelease);
    }

    [Fact]
    public void OldLongBetaTagsStillParseForTheVersionHistory() =>
        Assert.Equal("2.16.0-beta.18.29", UpdateVersion.Parse("win-v2.16.0-beta.18.29")!.ToString());

    [Fact]
    public void BetaChannelSelectsTheNewestShortBetaAboveOldLongOnes()
    {
        var releases = UpdateChecker.ParseReleases("""
            [
              { "tag_name": "win-v4.2.0-beta.40.12.3", "draft": false, "prerelease": true, "assets": [] },
              { "tag_name": "win-v4.2.0-beta010", "draft": false, "prerelease": true, "assets": [] },
              { "tag_name": "win-v4.2.0-beta009", "draft": false, "prerelease": true, "assets": [] },
              { "tag_name": "win-v4.1.1", "draft": false, "prerelease": false, "assets": [] }
            ]
            """);
        Assert.Equal("4.2.0-beta010", UpdateChecker.SelectLatestRelease(releases, true)!.DisplayVersion);
        Assert.Equal("4.1.1", UpdateChecker.SelectLatestRelease(releases, false)!.DisplayVersion);
        Assert.True(releases.Single(r => r.TagName == "win-v4.2.0-beta010").IsPreview);
    }
}

public sealed class BuildInfoTests
{
    private static readonly DateTimeOffset Built = new(2026, 11, 4, 14, 25, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

    private static BuildInfo Beta() => BuildInfo.Create("4.2.0-beta001", "35", "4E68C2A9D1E2F3A4B5C6D7E8F9A0B1C2D3E4F5A6",
        "2026-11-04T14:25:00Z", "win-v4.2.0-beta001", "installer, per-user");

    [Fact]
    public void BetaBuildDescribesItsNumberChannelAndSource()
    {
        var info = Beta();
        Assert.True(info.IsBeta);
        Assert.Equal("Beta", info.Channel);
        Assert.Equal("001", info.BetaNumber);
        Assert.Equal("4.2.0", info.CoreVersion);
        Assert.Equal("Version 4.2.0-beta001 · Beta · Windows", info.Subtitle());
        Assert.Equal("This is a test build. It is beta 001 of Evict 4.2.0, made from a pull request.", info.BetaBanner);
        Assert.Equal("https://github.com/krishnabhunia/evict-uninstaller/pull/35", info.PullRequestUrl);
        Assert.Equal("4e68c2a", info.CommitText);
        Assert.Equal("Evict_4.2.0-beta001.zip", info.DownloadName);
        Assert.Equal("04-Nov-2026, 19:55", info.BuiltOnText(India));
    }

    [Fact]
    public void ThisBuildTableListsTheItemsInOrder()
    {
        var rows = Beta().Rows(India);
        Assert.Equal(new[] { "Version", "Channel", "Made from", "Commit", "Built on", "GitHub tag", "Download", "Installed as" },
            rows.Select(r => r.Item));
        Assert.Equal("PR #35", rows.Single(r => r.Item == "Made from").Value);
        Assert.Equal("win-v4.2.0-beta001", rows.Single(r => r.Item == "GitHub tag").Value);
    }

    [Fact]
    public void CopyBuildInfoHasTwoLines()
    {
        var text = Beta().CopyText("Windows 11 Pro 24H2 x64", ".NET 8");
        Assert.Equal("Evict 4.2.0-beta001 (Beta) · PR #35 · 4e68c2a · built 2026-11-04 14:25 UTC" + Environment.NewLine +
                     "Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8", text);
    }

    [Fact]
    public void LocalBuildSaysLocalBuildWhereCiDataIsMissing()
    {
        var info = BuildInfo.Create("4.1.1", null, null, null, null, "portable");
        Assert.True(info.IsLocalBuild);
        Assert.False(info.IsBeta);
        Assert.Null(info.BetaBanner);
        Assert.Equal("Stable", info.Channel);
        var rows = info.Rows();
        Assert.DoesNotContain(rows, r => r.Item == "Made from");
        Assert.Equal(BuildInfo.LocalBuild, rows.Single(r => r.Item == "Commit").Value);
        Assert.Equal(BuildInfo.LocalBuild, rows.Single(r => r.Item == "Built on").Value);
        Assert.Equal(BuildInfo.LocalBuild, rows.Single(r => r.Item == "GitHub tag").Value);
        Assert.StartsWith("Evict 4.1.1 (Stable) · local build" + Environment.NewLine, info.CopyText("Windows 10 x64", ".NET 8"));
    }

    [Fact]
    public void StableMainBuildHasNoPullRequest()
    {
        var info = BuildInfo.Create("4.2.0", "", "0123456789abcdef0123456789abcdef01234567", "2026-11-05T08:00:00Z", "win-v4.2.0", "installer, all users");
        Assert.Null(info.SourcePr);
        Assert.Equal("main branch", info.MadeFromText);
        Assert.Equal("0123456", info.CommitText);
        Assert.Equal("win-v4.2.0", info.TagText);
    }

    [Theory]
    [InlineData("4.2.0-beta.40.12.3")]
    [InlineData("4.2.0-beta000")]
    public void OldLongAndUnreservedBetasHaveNoNumber(string version)
    {
        var info = BuildInfo.Create(version, "12", null, null, null, "portable");
        Assert.True(info.IsBeta);
        Assert.Null(info.BetaNumber);
        Assert.Equal("This is a test build of Evict 4.2.0, made from a pull request.", info.BetaBanner);
    }

    [Fact]
    public void MalformedMetadataIsIgnoredNotShown()
    {
        var info = BuildInfo.Create("4.2.0", "PR-7", "not-a-sha", "yesterday", "mac-v1.0.0", "portable");
        Assert.Null(info.SourcePr);
        Assert.Null(info.SourceCommit);
        Assert.Null(info.BuiltUtc);
        Assert.Null(info.ReleaseTag);
    }

    [Fact]
    public void CiMetadataIsReadFromAssemblyMetadataAttributes()
    {
        // Built by MSBuild from Directory.Build.props: local test builds carry the keys with empty values.
        var keys = typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Select(a => a.Key).ToHashSet();
        Assert.Superset(new HashSet<string> { "SourcePr", "SourceCommit", "BuildTimeUtc", "ReleaseTag" }, keys);
        var info = BuildInfo.FromAssembly(typeof(BuildInfo).Assembly, "4.2.0", "portable");
        Assert.Equal("4.2.0", info.Version);
    }

    [Theory]
    [InlineData(@"C:\Users\k\AppData\Local\Programs\Evict", null, @"c:\users\k\appdata\local\programs\evict\", "installer, per-user")]
    [InlineData(null, @"C:\Program Files\Evict", @"C:\Program Files\Evict", "installer, all users")]
    [InlineData(@"C:\Users\k\AppData\Local\Programs\Evict", @"C:\Program Files\Evict", @"D:\Tools", "portable")]
    [InlineData(null, null, @"D:\Tools", "portable")]
    public void InstallKindFollowsTheSetupMarkerForThisFolder(string? perUser, string? allUsers, string exe, string expected) =>
        Assert.Equal(expected, BuildInfo.DescribeInstall(perUser, allUsers, exe));

    [Theory]
    [InlineData("Windows 10 Pro", "24H2", 26100, true, "Windows 11 Pro 24H2 x64")]
    [InlineData("Windows 10 Home", "22H2", 19045, true, "Windows 10 Home 22H2 x64")]
    [InlineData(null, null, 19045, false, "Windows x86")]
    public void WindowsDescription(string? product, string? display, int build, bool x64, string expected) =>
        Assert.Equal(expected, BuildInfo.DescribeWindows(product, display, build, x64));

    [Fact]
    public void RuntimeDescription() => Assert.Equal(".NET 8", BuildInfo.DescribeRuntime(new Version(8, 0, 11)));
}

public sealed class InfoContentTests
{
    private const string Features = """
        Everything Evict can do.

        | SrNo. | Area | What it does | Since | Windows | Mac |
        |---|---|---|---|---|---|
        | 1 | Programs | Every installed program with its size. | 1.0.0 | Yes | Yes |
        | 2 | Windows Apps | Store apps for one or all users. | 1.0.0 | Yes | Not yet |
        | 3 | Info window | Shows this build and what is new. | 4.2.0 | Yes | Yes |
        | x | Broken | Not a number. | 1.0.0 | Yes | Yes |
        | 5 | Broken | Bad version. | Build 4 | Yes | Yes |
        """;

    [Fact]
    public void FeatureTableRowsAreParsedAndMalformedRowsSkipped()
    {
        var rows = InfoContent.ParseFeatures(Features);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.SrNo));
        Assert.Equal(new FeatureRow(2, "Windows Apps", "Store apps for one or all users.", "1.0.0", true, false), rows[1]);
        Assert.Equal("Not yet", rows[1].MacText);
    }

    [Fact]
    public void WrongHeaderOrMissingTextGivesNoRows()
    {
        Assert.Empty(InfoContent.ParseFeatures(null));
        Assert.Empty(InfoContent.ParseFeatures("| No. | Area | What it does | Since |\n|---|---|---|---|\n| 1 | A | B | 1.0.0 |"));
    }

    [Fact]
    public void SearchAndNewInThisVersionFilter()
    {
        var rows = InfoContent.ParseFeatures(Features);
        Assert.Equal(new[] { 3 }, InfoContent.FilterFeatures(rows, null, FeatureFilter.NewInThisVersion, "4.2.0").Select(r => r.SrNo));
        Assert.Empty(InfoContent.FilterFeatures(rows, null, FeatureFilter.NewInThisVersion, "4.2.1"));
        Assert.Equal(new[] { 2 }, InfoContent.FilterFeatures(rows, "store USERS", FeatureFilter.All, "4.2.0").Select(r => r.SrNo));
        Assert.Equal(new[] { 1, 3 }, InfoContent.FilterFeatures(rows, "", FeatureFilter.All, "4.2.0", mac: true).Select(r => r.SrNo));
        Assert.Equal(3, InfoContent.FilterFeatures(rows, "  ", FeatureFilter.All, "4.2.0").Count);
    }

    private const string Changelog = """
        # Changelog

        ## 4.2.0 — 2026-11-05

        <!-- Evict automatic release; base: win-v4.1.1 -->

        - Claude : Feature: short beta names and **Info** window
        - Uses `docs/FEATURES.md`

        ## 4.1.1 — 2026-11-03

        - ChatGPT-Evict-Version-Manager
        """;

    [Fact]
    public void ChangelogSectionOfTheVersionWithoutGeneratorMarkup()
    {
        var section = InfoContent.ChangelogFor(Changelog, "4.2.0")!;
        Assert.Equal("4.2.0 — 2026-11-05", section.Heading);
        Assert.Equal(new[] { "• Claude : Feature: short beta names and Info window", "• Uses docs/FEATURES.md" }, section.Lines);
        Assert.Equal(new[] { "• ChatGPT-Evict-Version-Manager" }, InfoContent.ChangelogFor(Changelog, "4.1.1")!.Lines);
        Assert.Null(InfoContent.ChangelogFor(Changelog, "4.1"));
        Assert.Null(InfoContent.ChangelogFor(Changelog, "9.9.9"));
        Assert.Null(InfoContent.ChangelogFor(null, "4.2.0"));
    }

    [Fact]
    public void OlderHeadingStylesWithTitlesAreFound()
    {
        var section = InfoContent.ChangelogFor("## 1.8.0 — close running programs (29 Sep 2026)\n\n- **A running program is closed.**\n  more text\n", "1.8.0")!;
        Assert.Equal("1.8.0 — close running programs (29 Sep 2026)", section.Heading);
        Assert.Equal(new[] { "• A running program is closed.", "  more text" }, section.Lines);
    }

    private static string RepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "docs", "FEATURES.md"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(new[] { directory!.FullName }.Concat(parts).ToArray()));
    }

    [Fact]
    public void TheEmbeddedRepositoryDocumentsParse()
    {
        var features = InfoContent.ParseFeatures(RepositoryFile("docs", "FEATURES.md"));
        Assert.True(features.Count >= 40);
        Assert.Equal(Enumerable.Range(1, features.Count), features.Select(f => f.SrNo));
        Assert.Contains(features, f => f.Area == "Info window" && f.OnWindows);

        var props = RepositoryFile("windows", "Directory.Build.props");
        var version = Regex.Match(props, @"<Version>\s*([^<]+?)\s*</Version>").Groups[1].Value;
        Assert.NotNull(InfoContent.ChangelogFor(RepositoryFile("windows", "CHANGELOG.md"), version));
    }
}
