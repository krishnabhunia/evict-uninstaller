using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

// These are isolated transport/handoff fixtures. They never execute an installer or claim a GUI upgrade.
public sealed class InstalledUpdateScenarioTests : IDisposable
{
    private const string Stable = "1.10.0";
    private const string Beta = "1.11.0-beta.13.123.1";
    private const string InstalledBeta = "1.9.0-beta.11.27.1";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EvictInstalledUpdateTests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("isolated installer fixture; never executed");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledBetaUpgradesToPublishedStableOnEitherChannel(bool includeBeta)
    {
        await AssertInstalledHandoff(InstalledBeta, includeBeta, Stable,
            ReleaseJson(InstalledBeta), ReleaseJson(Stable));
    }

    [Fact]
    public async Task InstalledStableDoesNotOfferOrDownloadANewerBetaWithoutOptIn()
    {
        using var handler = Feed(ReleaseJson(Stable), ReleaseJson(Beta));
        using var client = new HttpClient(handler);
        var launches = new List<ProcessStartInfo>();
        var service = new UpdateService(client, _directory, launches.Add, Stable);

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal(Stable, result.Release!.DisplayVersion);
        Assert.Equal(new[] { UpdateChecker.ReleasesApiUrl }, handler.Requests);
        Assert.Empty(launches);
        AssertNoDownload();
    }

    [Fact]
    public async Task InstalledStableOptInSelectsAndDownloadsTheFullBetaInstallerIdentity()
    {
        await AssertInstalledHandoff(Stable, true, Beta, ReleaseJson(Stable), ReleaseJson(Beta));
    }

    [Theory]
    [InlineData(Stable, false)]
    [InlineData(Beta, true)]
    public async Task CorruptInstalledUpdateIsRejectedWithoutPartialFilesOrInstallerLaunch(string candidate, bool includeBeta)
    {
        using var handler = Feed(new[] { ReleaseJson(candidate) }, corruptInstaller: true);
        using var client = new HttpClient(handler);
        var launches = new List<ProcessStartInfo>();
        var service = new UpdateService(client, _directory, launches.Add, InstalledBeta);
        var result = await service.CheckAsync(CancellationToken.None, includeBeta);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        var release = Assert.IsType<ReleaseInfo>(result.Release);
        var asset = Assert.IsType<ReleaseAsset>(UpdateChecker.PickAsset(release, installedMode: true));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(release, asset, null, CancellationToken.None));

        AssertInstalledRequests(handler, release, asset);
        Assert.Empty(launches);
        AssertNoDownload();
    }

    [Theory]
    [InlineData(Stable, false)]
    [InlineData(Beta, true)]
    public async Task CancellingInstalledUpdateTransferLeavesNoInstallableFileOrLaunch(string candidate, bool includeBeta)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = Feed(ReleaseJson(candidate));
        using var client = new HttpClient(handler);
        var launches = new List<ProcessStartInfo>();
        var service = new UpdateService(client, _directory, launches.Add, InstalledBeta);
        var result = await service.CheckAsync(cancellation.Token, includeBeta);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        var release = Assert.IsType<ReleaseInfo>(result.Release);
        var asset = Assert.IsType<ReleaseAsset>(UpdateChecker.PickAsset(release, installedMode: true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(release, asset, new CancelOnProgress(cancellation), cancellation.Token));

        AssertInstalledRequests(handler, release, asset);
        Assert.Empty(launches);
        AssertNoDownload();
    }

    private async Task AssertInstalledHandoff(string current, bool includeBeta, string expected, params string[] releases)
    {
        using var handler = Feed(releases);
        using var client = new HttpClient(handler);
        var events = new List<string>();
        var launches = new List<ProcessStartInfo>();
        var service = new UpdateService(client, _directory, info => { events.Add("launch"); launches.Add(info); }, current);

        var result = await service.CheckAsync(CancellationToken.None, includeBeta);
        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(current, service.CurrentVersionLabel);
        var release = Assert.IsType<ReleaseInfo>(result.Release);
        Assert.Equal("win-v" + expected, release.TagName);
        Assert.Equal(expected, release.DisplayVersion);
        Assert.Equal(expected.Contains('-'), release.IsPreview);
        var asset = Assert.IsType<ReleaseAsset>(UpdateChecker.PickAsset(release, installedMode: true));
        Assert.Equal("Evict-Setup-" + expected + ".exe", asset.Name);

        var downloaded = await service.DownloadAsync(release, asset, null, CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, asset.Name), downloaded);
        Assert.Equal(Installer, await File.ReadAllBytesAsync(downloaded));
        Assert.False(File.Exists(downloaded + ".partial"));
        AssertInstalledRequests(handler, release, asset);
        Assert.Empty(launches); // Checking and verified downloading never start Setup.

        var applied = service.Apply(downloaded, installedMode: true,
            beforeRestart: () => events.Add("release"), restartFailed: () => events.Add("recover"));

        Assert.True(applied.Ok);
        Assert.Null(applied.Error);
        Assert.Equal(new[] { "release", "launch" }, events);
        var launch = Assert.Single(launches);
        Assert.Equal(downloaded, launch.FileName);
        Assert.Equal("/SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1", launch.Arguments);
        Assert.True(launch.UseShellExecute);
        Assert.Equal(_directory, launch.WorkingDirectory);
    }

    private static void AssertInstalledRequests(FixtureHttpHandler handler, ReleaseInfo release, ReleaseAsset asset)
    {
        var checksum = Assert.IsType<ReleaseAsset>(UpdateChecker.PickChecksumAsset(release, asset));
        Assert.Equal(asset.Name + ".sha256", checksum.Name);
        Assert.Equal(new[] { UpdateChecker.ReleasesApiUrl, checksum.DownloadUrl, asset.DownloadUrl }, handler.Requests);
    }

    private static string ReleaseJson(string version)
    {
        var prefix = UpdateChecker.RepoUrl + "/releases/download/win-v" + version + "/";
        var setup = "Evict-Setup-" + version + ".exe";
        return JsonSerializer.Serialize(new
        {
            tag_name = "win-v" + version, name = "Evict " + version, draft = false,
            prerelease = version.Contains('-'), published_at = "2026-10-07T12:00:00Z",
            assets = new[]
            {
                new { name = "Evict.exe", browser_download_url = prefix + "Evict.exe", size = Installer.Length },
                new { name = "Evict.exe.sha256", browser_download_url = prefix + "Evict.exe.sha256", size = 64 },
                new { name = setup, browser_download_url = prefix + setup, size = Installer.Length },
                new { name = setup + ".sha256", browser_download_url = prefix + setup + ".sha256", size = 64 },
            },
        });
    }

    private static FixtureHttpHandler Feed(params string[] releases) => Feed(releases, false);

    private static FixtureHttpHandler Feed(string[] releases, bool corruptInstaller)
    {
        var json = "[" + string.Join(",", releases) + "]";
        var hash = Convert.ToHexString(SHA256.HashData(Installer));
        return new FixtureHttpHandler(uri =>
        {
            if (uri == UpdateChecker.ReleasesApiUrl)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            var filename = Path.GetFileName(new Uri(uri).AbsolutePath);
            if (!filename.StartsWith("Evict-Setup-", StringComparison.Ordinal))
                throw new InvalidOperationException("Installed update requested a portable or unexpected asset: " + uri);
            if (filename.EndsWith(".exe.sha256", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hash + "  " + filename[..^7] + "\r\n") };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corruptInstaller ? Encoding.UTF8.GetBytes("corrupted installer fixture") : Installer),
            };
        });
    }

    private void AssertNoDownload() => Assert.Empty(Directory.Exists(_directory) ? Directory.GetFiles(_directory) : Array.Empty<string>());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixtureHttpHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            Requests.Add(uri);
            return Task.FromResult(respond(uri));
        }
    }

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => cancellation.Cancel();
    }
}
