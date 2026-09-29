using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class Build3Tests
{
    // ───────────── version parsing / comparison ─────────────

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v2", "2.0.0")]
    [InlineData("release-1.2.3", "1.2.3")]
    [InlineData("v1.2.0-beta.1", "1.2.0")]
    [InlineData("v1.2.0.7", "1.2.0")]
    [InlineData("  V10.20.30 ", "10.20.30")]
    public void ParseVersion_HandlesCommonTagShapes(string tag, string expected)
    {
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData("v")]
    public void ParseVersion_ReturnsNullWithoutDigits(string? tag)
    {
        Assert.Null(UpdateChecker.ParseVersion(tag));
    }

    [Fact]
    public void IsNewer_IgnoresRevisionAndComparesNumerically()
    {
        Assert.True(UpdateChecker.IsNewer(new Version(1, 1, 0, 0), new Version(1, 2, 0)));
        Assert.True(UpdateChecker.IsNewer(new Version(1, 9, 0), new Version(1, 10, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(1, 2, 0, 0), new Version(1, 2, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(1, 2, 1), new Version(1, 2, 0)));
        Assert.False(UpdateChecker.IsNewer(new Version(2, 0, 0), new Version(1, 99, 99)));
    }

    [Fact]
    public void CurrentVersion_ComesFromAssemblyAndHasThreeParts()
    {
        var v = UpdateChecker.CurrentVersion;
        Assert.True(v.Major >= 1);
        Assert.Equal(-1, v.Revision);
        Assert.True(v.Build >= 0);
    }

    // ───────────── release JSON ─────────────

    private const string SampleRelease = """
        {
          "url": "https://api.github.com/repos/krishnabhunia/evict/releases/1",
          "html_url": "https://github.com/krishnabhunia/evict/releases/tag/v1.3.0",
          "tag_name": "v1.3.0",
          "name": "Evict 1.3.0",
          "draft": false,
          "prerelease": false,
          "published_at": "2026-10-01T10:15:00Z",
          "body": "## What's Changed\n* Faster scans\n* Bug fixes",
          "assets": [
            { "name": "Evict.exe", "browser_download_url": "https://github.com/krishnabhunia/evict/releases/download/v1.3.0/Evict.exe", "size": 69000000 },
            { "name": "Evict.exe.sha256", "browser_download_url": "https://github.com/krishnabhunia/evict/releases/download/v1.3.0/Evict.exe.sha256", "size": 66 },
            { "name": "Evict-Setup-1.3.0.exe", "browser_download_url": "https://github.com/krishnabhunia/evict/releases/download/v1.3.0/Evict-Setup-1.3.0.exe", "size": 40000000 },
            { "name": "Evict-Setup-1.3.0.exe.sha256", "browser_download_url": "https://github.com/krishnabhunia/evict/releases/download/v1.3.0/Evict-Setup-1.3.0.exe.sha256", "size": 66 }
          ]
        }
        """;

    [Fact]
    public void ParseRelease_ReadsVersionNotesAndAssets()
    {
        var r = UpdateChecker.ParseRelease(SampleRelease);
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 3, 0), r!.Version);
        Assert.Equal("v1.3.0", r.TagName);
        Assert.Equal("Evict 1.3.0", r.Name);
        Assert.Contains("Faster scans", r.Body);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 15, 0, TimeSpan.Zero), r.PublishedAt);
        Assert.False(r.Prerelease);
        Assert.Equal(4, r.Assets.Count);
        Assert.Equal(69000000, r.Assets[0].Size);
    }

    [Fact]
    public void ParseRelease_RejectsDraftsAndVersionlessTags()
    {
        Assert.Null(UpdateChecker.ParseRelease(SampleRelease.Replace("\"draft\": false", "\"draft\": true")));
        Assert.Null(UpdateChecker.ParseRelease(SampleRelease.Replace("\"tag_name\": \"v1.3.0\"", "\"tag_name\": \"nightly\"")));
        Assert.Null(UpdateChecker.ParseRelease("[]"));
        Assert.Null(UpdateChecker.ParseRelease(""));
    }

    [Fact]
    public void ParseRelease_ToleratesMissingOptionalFields()
    {
        var r = UpdateChecker.ParseRelease("""{ "tag_name": "v9.9.9" }""");
        Assert.NotNull(r);
        Assert.Equal("v9.9.9", r!.Name);
        Assert.Null(r.Body);
        Assert.Null(r.PublishedAt);
        Assert.Empty(r.Assets);
        Assert.Equal(UpdateChecker.ReleasesUrl, r.HtmlUrl);
    }

    // ───────────── asset selection ─────────────

    [Fact]
    public void PickAsset_InstalledGetsSetupPortableGetsExe()
    {
        var r = UpdateChecker.ParseRelease(SampleRelease)!;
        Assert.Equal("Evict-Setup-1.3.0.exe", UpdateChecker.PickAsset(r, installedMode: true)!.Name);
        Assert.Equal("Evict.exe", UpdateChecker.PickAsset(r, installedMode: false)!.Name);
        Assert.Equal("Evict.exe.sha256", UpdateChecker.PickChecksumAsset(r, UpdateChecker.PickAsset(r, false)!)!.Name);
        Assert.Equal("Evict-Setup-1.3.0.exe.sha256", UpdateChecker.PickChecksumAsset(r, UpdateChecker.PickAsset(r, true)!)!.Name);
    }

    [Fact]
    public void PickAsset_ReturnsNullWhenEditionIsMissing()
    {
        var r = UpdateChecker.ParseRelease("""{ "tag_name": "v1.3.0", "assets": [ { "name": "Evict.exe", "browser_download_url": "https://x/Evict.exe", "size": 1 } ] }""")!;
        Assert.Null(UpdateChecker.PickAsset(r, installedMode: true));
        Assert.NotNull(UpdateChecker.PickAsset(r, installedMode: false));
        Assert.Null(UpdateChecker.PickChecksumAsset(r, r.Assets[0]));
    }

    // ───────────── redirect fallback ─────────────

    [Theory]
    [InlineData("https://github.com/krishnabhunia/evict/releases/tag/v1.3.0", "v1.3.0")]
    [InlineData("/krishnabhunia/evict/releases/tag/v1.3.0?x=1", "v1.3.0")]
    [InlineData("https://github.com/o/r/releases/tag/release-2.0", "release-2.0")]
    [InlineData("https://github.com/o/r/releases", null)]
    [InlineData("", null)]
    public void ParseTagFromRedirect_ExtractsTag(string location, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.ParseTagFromRedirect(location));
    }

    [Fact]
    public void ReleaseFromTag_BuildsGitHubDownloadUrls()
    {
        var r = UpdateChecker.ReleaseFromTag("v1.3.0");
        Assert.NotNull(r);
        Assert.Equal(new Version(1, 3, 0), r!.Version);
        var exe = UpdateChecker.PickAsset(r, false)!;
        Assert.Equal("https://github.com/krishnabhunia/evict/releases/download/v1.3.0/Evict.exe", exe.DownloadUrl);
        Assert.Equal("Evict-Setup-1.3.0.exe", UpdateChecker.PickAsset(r, true)!.Name);
        Assert.Null(UpdateChecker.ReleaseFromTag("nightly"));
    }

    // ───────────── checksum files ─────────────

    [Theory]
    [InlineData("A2ED5E5DB90978BC2540289C5CCE0A4B6711F49AB71B6DEC9F053AE33FB8A9E0\r\n")]
    [InlineData("a2ed5e5db90978bc2540289c5cce0a4b6711f49ab71b6dec9f053ae33fb8a9e0  Evict.exe\n")]
    [InlineData("SHA256 (Evict.exe) = a2ed5e5db90978bc2540289c5cce0a4b6711f49ab71b6dec9f053ae33fb8a9e0")]
    [InlineData("a2ed5e5db90978bc2540289c5cce0a4b6711f49ab71b6dec9f053ae33fb8a9e0 *Evict.exe")]
    public void ParseSha256_FindsTheHexDigest(string text)
    {
        Assert.Equal("a2ed5e5db90978bc2540289c5cce0a4b6711f49ab71b6dec9f053ae33fb8a9e0", UpdateChecker.ParseSha256(text));
    }

    [Fact]
    public void ParseSha256_RejectsGarbage()
    {
        Assert.Null(UpdateChecker.ParseSha256("not a hash"));
        Assert.Null(UpdateChecker.ParseSha256("zzed5e5db90978bc2540289c5cce0a4b6711f49ab71b6dec9f053ae33fb8a9e0"));
        Assert.Null(UpdateChecker.ParseSha256(null));
    }

    // ───────────── installed-mode detection ─────────────

    [Theory]
    [InlineData(@"C:\Program Files\Evict\", @"c:\program files\evict", true)]
    [InlineData(@"C:\Program Files\Evict", @"C:/Program Files/Evict/", true)]
    [InlineData(@"C:\Program Files\Evict", @"C:\Tools\Evict", false)]
    [InlineData(null, @"C:\Tools\Evict", false)]
    [InlineData("", "", false)]
    public void SameDirectory_IsCaseAndSeparatorInsensitive(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.SameDirectory(a, b));
    }

    [Fact]
    public void Describe_ShowsBothVersions()
    {
        Assert.Equal("1.1.0 → 1.2.0", UpdateChecker.Describe(new Version(1, 1, 0, 0), new Version(1, 2, 0)));
    }

    // ───────────── command line ─────────────

    [Fact]
    public void CommandLine_ParsesUpdatedFlag()
    {
        var o = CommandLineOptions.Parse(new[] { "--updated" });
        Assert.True(o.Updated);
        Assert.False(o.IsEmpty);
        Assert.False(CommandLineOptions.Parse(new[] { "--widget" }).Updated);
    }

    [Fact]
    public void Settings_UpdateDefaults()
    {
        var s = new AppSettings();
        Assert.True(s.CheckForUpdates);
        Assert.Null(s.LastUpdateCheckUtc);
        Assert.Null(s.SkippedUpdateVersion);
    }
}
