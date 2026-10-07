using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Evict.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Evict.Core.Tests;

/// <summary>
/// Explicitly opted-in CI validation of published metadata and real Setup bytes.
/// Installer launch is captured; this does not execute Setup, detect a registry install, or test the GUI.
/// </summary>
public sealed class PublishedUpdateScenarioTests(ITestOutputHelper output)
{
    private const string VersionVariable = "EVICT_PUBLISHED_UPDATE_TEST_VERSION";
    private const string ChannelVariable = "EVICT_PUBLISHED_UPDATE_TEST_CHANNEL";

    [Fact]
    [Trait("Category", "PublishedUpdate")]
    public async Task PublishedInstalledUpdateMatchesTheRequestedChannelAndVerifiedInstaller()
    {
        var version = Environment.GetEnvironmentVariable(VersionVariable)?.Trim();
        var channel = Environment.GetEnvironmentVariable(ChannelVariable)?.Trim();
        if (string.IsNullOrEmpty(version) && string.IsNullOrEmpty(channel))
        {
            output.WriteLine("NOT RUN: live published-update validation requires both opt-in environment variables. No live release or installer was validated.");
            return;
        }

        Assert.False(string.IsNullOrEmpty(version), VersionVariable + " is required for opted-in validation.");
        Assert.True(channel is "stable" or "beta", ChannelVariable + " must be stable or beta.");
        Assert.True(OperatingSystem.IsWindows(), "This opted-in installed-update scenario requires Windows CI.");
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));

        var expected = Assert.IsType<UpdateVersion>(UpdateVersion.Parse(version));
        Assert.Equal(version, expected.ToString());
        var betaChannel = channel == "beta";
        Assert.Equal(betaChannel, expected.IsPrerelease);

        var expectedTag = "win-v" + version;

        var runnerTemp = Environment.GetEnvironmentVariable("RUNNER_TEMP");
        Assert.False(string.IsNullOrWhiteSpace(runnerTemp), "RUNNER_TEMP is required for isolated downloads.");
        var tempRoot = Path.GetFullPath(runnerTemp!);
        Assert.True(Directory.Exists(tempRoot), "RUNNER_TEMP must already exist.");
        var directory = Path.GetFullPath(Path.Combine(tempRoot, "EvictPublishedUpdateTests-" + Guid.NewGuid().ToString("N")));
        AssertOwnedPath(tempRoot, directory);
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Evict-Updater-CI/1.0 (+" + UpdateChecker.RepoUrl + ")");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            // Resolve the public stable baseline from GitHub instead of hardcoding a test version.
            // This also proves that beta and draft releases cannot become the default channel.
            var baselineProbe = new UpdateService(client, directory, _ =>
                throw new InvalidOperationException("Release discovery must not launch an installer."), "0.0.0");
            var baselineCheck = await baselineProbe.CheckAsync(timeout.Token);
            var stableBaseline = Assert.IsType<ReleaseInfo>(baselineCheck.Release);
            Assert.Equal(UpdateStatus.UpdateAvailable, baselineCheck.Status);
            Assert.False(stableBaseline.IsPreview);
            var current = betaChannel ? stableBaseline.DisplayVersion : "0.0.0";
            var launches = new List<ProcessStartInfo>();
            var events = new List<string>();
            var updater = new UpdateService(client, directory,
                info => { events.Add("launch"); launches.Add(info); }, current);

            if (betaChannel)
            {
                var stableOnly = await updater.CheckAsync(timeout.Token);
                output.WriteLine("Live beta opt-out: " + stableOnly.Message);
                Assert.Equal(UpdateStatus.UpToDate, stableOnly.Status);
                var stableRelease = Assert.IsType<ReleaseInfo>(stableOnly.Release);
                Assert.Equal(stableBaseline.TagName, stableRelease.TagName);
                Assert.Equal(stableBaseline.DisplayVersion, stableRelease.DisplayVersion);
                Assert.False(stableRelease.IsPreview);
                Assert.Empty(launches);
                Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            }

            var check = await updater.CheckAsync(timeout.Token, includePrereleases: betaChannel);
            output.WriteLine("Live requested-channel check: " + check.Message);
            Assert.Equal(UpdateStatus.UpdateAvailable, check.Status);
            Assert.Equal(current, updater.CurrentVersionLabel);
            var selected = Assert.IsType<ReleaseInfo>(check.Release);
            Assert.NotNull(selected.PublishedAt);
            // Parallel PRs can publish a newer preview while this release is being verified.
            // The normal updater must select the highest available version, not force this PR.
            Assert.True(selected.SemanticVersion.CompareTo(expected) >= 0,
                "The updater selected a version older than the newly published release.");
            if (!betaChannel)
            {
                Assert.False(selected.IsPreview);
                Assert.Equal(expectedTag, selected.TagName);
            }

            // Independently verify and download this exact PR's real release, even when another
            // PR preview is newer. No catalog response or installed-update launch is fabricated.
            var metadataUrl = "https://api.github.com/repos/" + UpdateChecker.RepoOwner + "/" +
                UpdateChecker.RepoName + "/releases/tags/" + Uri.EscapeDataString(expectedTag);
            var metadataJson = await client.GetStringAsync(metadataUrl, timeout.Token);
            using (var metadata = JsonDocument.Parse(metadataJson))
            {
                Assert.Equal(expectedTag, metadata.RootElement.GetProperty("tag_name").GetString());
                Assert.False(metadata.RootElement.GetProperty("draft").GetBoolean());
                Assert.Equal(betaChannel, metadata.RootElement.GetProperty("prerelease").GetBoolean());
            }
            var release = Assert.IsType<ReleaseInfo>(UpdateChecker.ParseRelease(metadataJson));
            Assert.Equal(expectedTag, release.TagName);
            Assert.Equal(version, release.DisplayVersion);
            Assert.Equal(betaChannel, release.IsPreview);
            Assert.NotNull(release.PublishedAt);
            output.WriteLine("Live selected release: " + selected.TagName +
                "; exact published installer under verification: " + release.TagName);

            var asset = Assert.IsType<ReleaseAsset>(UpdateChecker.PickAsset(release, installedMode: true));
            var checksum = Assert.IsType<ReleaseAsset>(UpdateChecker.PickChecksumAsset(release, asset));
            Assert.Equal("Evict-Setup-" + version + ".exe", asset.Name);
            Assert.Equal(asset.Name + ".sha256", checksum.Name);
            Assert.True(asset.Size > 0, "The published Setup asset must have nonzero size.");
            Assert.True(checksum.Size > 0, "The published Setup checksum must have nonzero size.");
            var downloadPrefix = UpdateChecker.RepoUrl + "/releases/download/" + expectedTag + "/";
            Assert.Equal(downloadPrefix + asset.Name, asset.DownloadUrl);
            Assert.Equal(downloadPrefix + checksum.Name, checksum.DownloadUrl);

            var checksumText = await client.GetStringAsync(checksum.DownloadUrl, timeout.Token);
            var expectedHash = UpdateChecker.ParseSha256(checksumText);
            Assert.NotNull(expectedHash);

            // DownloadAsync fetches the published sidecar again and rejects unverifiable bytes.
            var downloaded = await updater.DownloadAsync(release, asset, null, timeout.Token);
            AssertOwnedPath(tempRoot, downloaded);
            Assert.Equal(Path.Combine(directory, asset.Name), downloaded);
            Assert.False(File.Exists(downloaded + ".partial"));
            Assert.Equal(asset.Size, new FileInfo(downloaded).Length);
            var setupVersion = FileVersionInfo.GetVersionInfo(downloaded);
            Assert.Equal(expected.Core.Major, setupVersion.FileMajorPart);
            Assert.Equal(expected.Core.Minor, setupVersion.FileMinorPart);
            Assert.Equal(expected.Core.Build, setupVersion.FileBuildPart);
            await using (var stream = File.OpenRead(downloaded))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, timeout.Token)).ToLowerInvariant();
                Assert.Equal(expectedHash, actualHash);
            }
            Assert.Empty(launches); // Real metadata and downloads do not launch an installer.

            var applied = updater.Apply(downloaded, installedMode: true,
                beforeRestart: () => events.Add("release"), restartFailed: () => events.Add("recover"));
            Assert.True(applied.Ok, applied.Error);
            Assert.Null(applied.Error);
            Assert.Equal(new[] { "release", "launch" }, events);
            var launch = Assert.Single(launches);
            Assert.Equal(downloaded, launch.FileName);
            Assert.Equal("/SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1", launch.Arguments);
            Assert.True(launch.UseShellExecute);
            Assert.Equal(directory, launch.WorkingDirectory);

            output.WriteLine("VALIDATED LIVE: " + expectedTag + ", exact Setup asset and native version, published SHA-256 and captured installed-update handoff. Setup and the GUI were not executed.");
        }
        finally
        {
            RemoveOwnedDownloads(tempRoot, directory);
        }
    }

    private static void AssertOwnedPath(string tempRoot, string path)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempRoot)) + Path.DirectorySeparatorChar;
        Assert.True(Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            "Refusing a path outside RUNNER_TEMP.");
    }

    private static void RemoveOwnedDownloads(string tempRoot, string directory)
    {
        AssertOwnedPath(tempRoot, directory);
        if (!Directory.Exists(directory)) return;
        Assert.False((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0,
            "Refusing cleanup of a reparse-point download directory.");
        // This fixture creates only immediate download files; never recursively remove an unexpected directory.
        Assert.Empty(Directory.EnumerateDirectories(directory));
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            AssertOwnedPath(tempRoot, file);
            Assert.False((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0,
                "Refusing cleanup of a reparse-point download file.");
            File.Delete(file);
        }
        Directory.Delete(directory, recursive: false);
    }
}
