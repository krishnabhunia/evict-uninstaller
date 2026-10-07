using System.Net;
using System.Text.Json;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class ReleaseChannelTests
{
    [Theory]
    [InlineData("1.8.1-beta.10", "1.8.1-beta.9", 1)]
    [InlineData("1.8.1-beta.1", "1.8.1-beta", 1)]
    [InlineData("1.8.1", "1.8.1-beta.99", 1)]
    [InlineData("1.8.1-alpha", "1.8.1-9", 1)]
    [InlineData("1.8.1-beta.999999999999999999999999999999", "1.8.1-beta.99999999999999999999999999999", 1)]
    [InlineData("1.8.1-beta.A", "1.8.1-beta.a", -1)]
    [InlineData("1.8.1+first-sha", "1.8.1+second-sha", 0)]
    [InlineData("1.8.1-beta.1+sha-one", "1.8.1-beta.1+sha-two", 0)]
    public void SemanticPrecedenceIncludesNumericPrereleasesAndIgnoresBuildMetadata(string left, string right, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateVersion.Parse(left)!.CompareTo(UpdateVersion.Parse(right))));
    }

    [Theory]
    [InlineData("1.8-beta.1")]
    [InlineData("1.8.1-beta.01")]
    [InlineData("1.8.1-beta..1")]
    [InlineData("1.8.1+")]
    [InlineData("1.8.1-")]
    [InlineData("01.8.1")]
    [InlineData("9999999999999999999.0.0")]
    [InlineData("mac-v9.0.0")]
    [InlineData("linux-v9.0.0")]
    public void InvalidOrOtherPlatformVersionIdentitiesAreRejected(string value) => Assert.Null(UpdateVersion.Parse(value));

    [Fact]
    public void StableSelectionExcludesDraftsPreviewTagsAndGitHubPrereleaseFlags()
    {
        var releases = UpdateChecker.ParseReleases(ListJson(
            Release("win-v1.8.0"),
            Release("win-v1.9.0-beta.1", prerelease: false),
            Release("win-v1.9.0", prerelease: true),
            Release("win-v2.0.0", draft: true),
            Release("mac-v9.0.0"),
            Release("linux-v9.0.0")));

        Assert.Equal("1.8.0", UpdateChecker.SelectLatestRelease(releases)!.DisplayVersion);
        Assert.DoesNotContain(releases, release => release.TagName is "mac-v9.0.0" or "linux-v9.0.0" or "win-v2.0.0");
        Assert.True(releases.Single(release => release.TagName == "win-v1.9.0-beta.1").Prerelease);
    }

    [Fact]
    public void PreviewChannelIncludesStableAndBetaAndSelectsByVersionRatherThanPublicationDate()
    {
        var releases = UpdateChecker.ParseReleases(ListJson(
            Release("win-v1.8.1", publishedAt: "2026-10-10T00:00:00Z"),
            Release("win-v1.9.0-beta.9", publishedAt: "2026-10-11T00:00:00Z"),
            Release("win-v1.9.0-beta.10", publishedAt: "2026-10-09T00:00:00Z")));

        Assert.Equal("1.9.0-beta.10", UpdateChecker.SelectLatestRelease(releases, true)!.DisplayVersion);
        Assert.Equal("1.8.1", UpdateChecker.SelectLatestRelease(releases)!.DisplayVersion);
    }

    [Fact]
    public void StableOutranksItsBetaAndWindowsTagWinsAnEqualLegacyVersion()
    {
        var releases = UpdateChecker.ParseReleases(ListJson(
            Release("v1.9.0", publishedAt: "2026-10-11T00:00:00Z"),
            Release("win-v1.9.0", publishedAt: "2026-10-09T00:00:00Z"),
            Release("win-v1.9.0-beta.99")));

        Assert.Equal("win-v1.9.0", UpdateChecker.SelectLatestRelease(releases, true)!.TagName);
    }

    [Fact]
    public void MetadataHyphensDoNotMakeAStableReleaseABeta()
    {
        var release = UpdateChecker.ParseRelease(Release("win-v1.8.1+build-beta"))!;

        Assert.False(release.IsPreview);
        Assert.Equal("1.8.1", release.DisplayVersion);
        Assert.Same(release, UpdateChecker.SelectLatestRelease(new[] { release }));
    }

    [Fact]
    public void BetaInstallerFallbackRetainsItsFullVersionInTheAssetAndChecksumNames()
    {
        var release = UpdateChecker.ReleaseFromTag("win-v1.8.1-beta.10")!;
        var asset = UpdateChecker.PickAsset(release, installedMode: true)!;

        Assert.Equal("Evict-Setup-1.8.1-beta.10.exe", asset.Name);
        Assert.Equal(asset.Name + ".sha256", UpdateChecker.PickChecksumAsset(release, asset)!.Name);
    }

    [Theory]
    [InlineData("1.8.1-beta.1+build-sha", "win-v1.8.1-beta.2", true)]
    [InlineData("1.8.1-beta.2+build-sha", "win-v1.8.1", true)]
    [InlineData("1.8.1-beta.2", "win-v1.8.0", false)]
    [InlineData("1.8.1", "win-v1.8.1-beta.99", false)]
    [InlineData("1.8.1-beta.2+sha-one", "win-v1.8.1-beta.2+sha-two", false)]
    public void InstalledSemanticVersionAvoidsRepeatedUpdatesAndDowngrades(string current, string candidate, bool expected)
        => Assert.Equal(expected, UpdateChecker.IsNewer(current, UpdateChecker.ParseRelease(Release(candidate))!));

    [Fact]
    public async Task CheckDefaultsToStableAndOptInFindsANewerBeta()
    {
        using var handler = new FixtureHandler(_ => Response(ListJson(Release("win-v1.8.0"), Release("win-v1.8.1-beta.1", prerelease: true))));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var stable = await updater.CheckAsync(CancellationToken.None);
        var beta = await updater.CheckAsync(CancellationToken.None, includePrereleases: true);

        Assert.Equal(UpdateStatus.UpToDate, stable.Status);
        Assert.Equal("1.8.0", stable.Release!.DisplayVersion);
        Assert.Equal(UpdateStatus.UpdateAvailable, beta.Status);
        Assert.Equal("1.8.1-beta.1", beta.Release!.DisplayVersion);
        Assert.Contains("1.8.1-beta.1", beta.Message);
        Assert.All(handler.Requests, request => Assert.Equal(UpdateChecker.ReleasesApiUrl, request));
    }

    [Fact]
    public async Task StableReleaseOnTheSecondPageIsFoundBehindNewerMacAndBetaReleases()
    {
        using var handler = new FixtureHandler(request => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? Response(ListJson(Release("win-v1.8.1")))
            : Response(ListJson(Release("mac-v9.0.0"), Release("win-v1.9.0-beta.1")), NextPage(2)));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.8.1", result.Release!.DisplayVersion);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CanonicalGitHubRepositoryIdPaginationLinksAreFollowed()
    {
        const string canonical = "https://api.github.com/repositories/1372964211/releases?per_page=100&page=2";
        using var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/repositories/", StringComparison.Ordinal)
            ? Response(ListJson(Release("win-v1.8.1")))
            : Response(ListJson(Release("mac-v9.0.0")), canonical));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(canonical, handler.Requests[1]);
    }

    [Fact]
    public async Task AFailedLaterPageNeverReturnsAPartialWinner()
    {
        using var handler = new FixtureHandler(request => request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Response(ListJson(Release("win-v1.8.1")), NextPage(2)));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None, includePrereleases: true);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("http://api.github.com/repos/krishnabhunia/evict-uninstaller/releases?page=2")]
    [InlineData("https://fixtures.invalid/repos/krishnabhunia/evict-uninstaller/releases?page=2")]
    [InlineData("https://api.github.com.fixtures.invalid/repos/krishnabhunia/evict-uninstaller/releases?page=2")]
    [InlineData("https://api.github.com:8443/repos/krishnabhunia/evict-uninstaller/releases?page=2")]
    [InlineData("https://api.github.com/repos/another-owner/another-repo/releases?page=2")]
    [InlineData("https://api.github.com/repositories/123456/releases?page=2")]
    public async Task UntrustedPaginationLinkIsUnavailableWithoutFollowingIt(string next)
    {
        using var handler = new FixtureHandler(_ => Response(ListJson(Release("win-v1.8.1")), next));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None, true);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("missing-url; rel=\"next\"")]
    [InlineData("<https://api.github.com/repos/krishnabhunia/evict-uninstaller/releases?page=2>; rel=\"next")]
    public async Task IncompletePaginationMetadataNeverReportsAPartialWinner(string link)
    {
        using var handler = new FixtureHandler(_ =>
        {
            var response = Response(ListJson(Release("win-v1.8.1")));
            response.Headers.TryAddWithoutValidation("Link", link);
            return response;
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARepeatedPaginationUrlIsRejectedWithoutASecondRequest()
    {
        using var handler = new FixtureHandler(_ => Response(ListJson(Release("win-v1.8.1")), UpdateChecker.ReleasesApiUrl));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false, UpdateStatus.UpdateAvailable)]
    [InlineData(true, UpdateStatus.Unavailable)]
    public async Task PaginationLimitSucceedsOnlyWhenTheFinalPageIsComplete(bool extraPage, UpdateStatus expected)
    {
        int page = 0;
        using var handler = new FixtureHandler(_ =>
        {
            page++;
            return Response(ListJson(Release(page == 20 ? "win-v1.8.1" : "mac-v9.0.0")),
                page < 20 || extraPage ? NextPage(page + 1) : null);
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None, true);

        Assert.Equal(expected, result.Status);
        Assert.Equal(20, handler.Requests.Count);
    }

    [Fact]
    public async Task ReleaseApiFailureDoesNotSilentlyFallBackToADifferentChannel()
    {
        using var handler = new FixtureHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        var result = await updater.CheckAsync(CancellationToken.None, true);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.Null(result.Release);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FixtureHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, "unused", currentVersionLabel: "1.8.0");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.CheckAsync(cancellation.Token, true));
    }

    private static string Release(string tag, bool prerelease = false, bool draft = false, string? publishedAt = null) =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag, prerelease, draft, published_at = publishedAt, name = tag,
            assets = new[]
            {
                new { name = "Evict.exe", browser_download_url = "https://fixtures.invalid/Evict.exe", size = 1 },
                new { name = "Evict.exe.sha256", browser_download_url = "https://fixtures.invalid/Evict.exe.sha256", size = 64 },
            },
        });

    private static string ListJson(params string[] releases) => "[" + string.Join(",", releases) + "]";
    private static string NextPage(int page) => $"https://api.github.com/repos/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}/releases?per_page=100&page={page}";

    private static HttpResponseMessage Response(string content, string? next = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) };
        if (next != null) response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        return response;
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }
}
