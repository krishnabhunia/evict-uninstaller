using System.ComponentModel;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class UpdateReliabilityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EvictUpdateTests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Executable = Encoding.UTF8.GetBytes("isolated update fixture; never executed");
    private static readonly ReleaseAsset Binary = new("Evict.exe", "https://fixtures.invalid/Evict.exe", Executable.Length);
    private static readonly ReleaseAsset Checksum = new("Evict.exe.sha256", "https://fixtures.invalid/Evict.exe.sha256", 64);

    private static ReleaseInfo Release(params ReleaseAsset[] assets) =>
        new(new Version(9, 0, 0), "win-v9.0.0", "Fixture", "https://fixtures.invalid/release", null, null, false, assets);

    [Fact]
    public async Task MissingPublishedChecksumStopsBeforeDownloadingExecutable()
    {
        using var handler = new FixtureHttpHandler(_ => throw new InvalidOperationException("No HTTP request expected"));
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(Binary), Binary, null, CancellationToken.None));

        Assert.Empty(handler.Requests);
        AssertNoDownload();
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "missing")]
    [InlineData(HttpStatusCode.OK, "not a checksum")]
    public async Task UnavailableOrMalformedPublishedChecksumStopsBeforeExecutableRequest(HttpStatusCode status, string body)
    {
        using var handler = new FixtureHttpHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(Binary, Checksum), Binary, null, CancellationToken.None));

        Assert.Equal(new[] { Checksum.DownloadUrl }, handler.Requests);
        AssertNoDownload();
    }

    [Fact]
    public async Task ChecksumMismatchRemovesPartialFileAndDoesNotAcceptTheUpdate()
    {
        using var handler = new FixtureHttpHandler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = uri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(new string('0', 64)) : new ByteArrayContent(Executable),
        });
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAsync(Release(Binary, Checksum), Binary, null, CancellationToken.None));

        Assert.Equal(2, handler.Requests.Count);
        AssertNoDownload();
    }

    [Fact]
    public async Task MatchingPublishedChecksumAcceptsExactlyTheDownloadedBytes()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Executable));
        using var handler = new FixtureHttpHandler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = uri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(hash + "  Evict.exe\r\n") : new ByteArrayContent(Executable),
        });
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);

        var path = await service.DownloadAsync(Release(Binary, Checksum), Binary, null, CancellationToken.None);

        Assert.Equal(Path.Combine(_directory, "Evict-9.0.0.exe"), path);
        Assert.Equal(Executable, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task CancellationDuringChecksumFetchIsNotDowngradedToAnUnverifiedUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FixtureHttpHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(Release(Binary, Checksum), Binary, null, cancellation.Token));

        Assert.Single(handler.Requests);
        AssertNoDownload();
    }

    [Fact]
    public async Task CancelledExecutableTransferRemovesItsPartialDownload()
    {
        using var cancellation = new CancellationTokenSource();
        var hash = Convert.ToHexString(SHA256.HashData(Executable));
        using var handler = new FixtureHttpHandler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = uri.EndsWith(".sha256", StringComparison.Ordinal)
                ? new StringContent(hash) : new ByteArrayContent(Executable),
        });
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, _directory);
        var progress = new CancelOnProgress(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAsync(Release(Binary, Checksum), Binary, progress, cancellation.Token));

        Assert.Equal(2, handler.Requests.Count);
        AssertNoDownload();
    }

    [Theory]
    [InlineData(1223)] // UAC cancellation
    [InlineData(2)] // The installer disappeared before launch
    public void FailedInstalledUpdateHandoffRestoresOwnership(int nativeError)
    {
        var events = new List<string>();
        using var client = new HttpClient(new FixtureHttpHandler(_ => throw new InvalidOperationException("No network expected")));
        var service = new UpdateService(client, _directory, _ => { events.Add("launch"); throw new Win32Exception(nativeError); });

        var result = service.Apply(Path.Combine(_directory, "fake-setup.exe"), true,
            () => events.Add("release"), () => events.Add("recover"));

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Equal(new[] { "release", "launch", "recover" }, events);
    }

    [Fact]
    public void SuccessfulHandoffReleasesOwnershipWithoutRestartingTheOldArgumentServer()
    {
        var events = new List<string>();
        using var client = new HttpClient(new FixtureHttpHandler(_ => throw new InvalidOperationException("No network expected")));
        var service = new UpdateService(client, _directory, info =>
        {
            Assert.Contains("/EVICTUPDATE=1", info.Arguments);
            events.Add("launch");
        });

        var result = service.Apply(Path.Combine(_directory, "fake-setup.exe"), true,
            () => events.Add("release"), () => events.Add("recover"));

        Assert.True(result.Ok);
        Assert.Equal(new[] { "release", "launch" }, events);
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
