using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class UpdateNetworkDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EvictNetworkTests-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("unexecuted fixture");
    private static readonly ReleaseAsset Binary = new("Evict.exe", "https://fixtures.invalid/Evict.exe", Bytes.Length);
    private static readonly ReleaseAsset Checksum = new("Evict.exe.sha256", "https://fixtures.invalid/Evict.exe.sha256", 64);
    private static ReleaseInfo Release => new(new Version(9, 0, 0), "win-v9.0.0", "Fixture",
        "https://fixtures.invalid/release", null, null, false, new[] { Binary, Checksum });
    private static Exception Denied() => new HttpRequestException("Connection failed", new SocketException(10013));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void SocketPermissionDenialIsFoundThroughWrappedExceptions(int depth)
    {
        Exception failure = new SocketException(10013);
        for (var i = 0; i < depth; i++) failure = new IOException("Outer failure", failure);
        Assert.True(UpdateNetworkDiagnostics.IsSocketAccessDenied(failure));
    }

    [Fact]
    public void AggregateFailureInspectsAllInnerExceptions()
    {
        var failure = new AggregateException(new IOException("Unrelated"), Denied());
        Assert.True(UpdateNetworkDiagnostics.IsSocketAccessDenied(failure));
    }

    [Theory]
    [InlineData(10061)]
    [InlineData(10060)]
    [InlineData(11001)]
    public void OtherSocketErrorsAreNotCalledFirewallPermissionDenials(int code) =>
        Assert.False(UpdateNetworkDiagnostics.IsSocketAccessDenied(new SocketException(code)));

    [Fact]
    public void FilePermissionsAreNotCalledSocketPermissionDenials() =>
        Assert.False(UpdateNetworkDiagnostics.IsSocketAccessDenied(new UnauthorizedAccessException("File is protected")));

    [Fact]
    public async Task CheckPermissionFailureReportsUnavailableWithActionableRecovery()
    {
        using var handler = new Handler(_ => throw Denied());
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, _directory, currentVersionLabel: "1.12.2");

        var result = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.True(result.NetworkAccessBlocked);
        Assert.Null(result.Release);
        AssertRecovery(result.Message);
        Assert.Equal(new[] { UpdateChecker.ReleasesApiUrl }, handler.Requests);
        AssertNoDownloads();
    }

    [Fact]
    public async Task RemoteRateLimitIsNotPresentedAsAnApplicationFirewallRule()
    {
        using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var client = new HttpClient(handler);
        var result = await new UpdateService(client, _directory).CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.Unavailable, result.Status);
        Assert.False(result.NetworkAccessBlocked);
        Assert.Contains("rate limit", result.Message);
    }

    [Fact]
    public async Task CancellationIsNotReportedAsPermissionDenial()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, _directory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.CheckAsync(cancellation.Token));
        AssertNoDownloads();
    }

    [Fact]
    public async Task ChecksumPermissionFailureStopsBeforeRequestingExecutable()
    {
        using var handler = new Handler(_ => throw Denied());
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, _directory);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.DownloadAsync(Release, Binary, null, CancellationToken.None));

        AssertRecovery(error.Message);
        Assert.True(UpdateNetworkDiagnostics.IsSocketAccessDenied(error));
        Assert.Equal(new[] { Checksum.DownloadUrl }, handler.Requests);
        AssertNoDownloads();
    }

    [Fact]
    public async Task DeniedConnectionMidDownloadDeletesPartialAndDoesNotLaunchInstaller()
    {
        var sawPartial = false;
        var launches = 0;
        using var handler = new Handler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = uri == Checksum.DownloadUrl
                ? new StringContent(Convert.ToHexString(SHA256.HashData(Bytes)))
                : new StreamContent(new DeniedStream(Bytes)),
        });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, _directory, _ => launches++);
        var progress = new CaptureProgress(() => sawPartial = File.Exists(Path.Combine(_directory, "Evict-9.0.0.exe.partial")));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.DownloadAsync(Release, Binary, progress, CancellationToken.None));

        Assert.True(sawPartial);
        AssertRecovery(error.Message);
        Assert.True(UpdateNetworkDiagnostics.IsSocketAccessDenied(error));
        Assert.Equal(new[] { Checksum.DownloadUrl, Binary.DownloadUrl }, handler.Requests);
        Assert.Equal(0, launches);
        AssertNoDownloads();
    }

    [Fact]
    public async Task RetryAfterNetworkPermissionRecoveryFindsNewReleaseAndClearsBlockedFlag()
    {
        var request = 0;
        using var handler = new Handler(_ => ++request == 1
            ? throw Denied()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[{\"tag_name\":\"win-v1.12.3\",\"draft\":false,\"prerelease\":false,\"assets\":[]}]"),
            });
        using var client = new HttpClient(handler);
        var updater = new UpdateService(client, _directory, currentVersionLabel: "1.12.2");

        Assert.True((await updater.CheckAsync(CancellationToken.None)).NetworkAccessBlocked);
        var recovered = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.UpdateAvailable, recovered.Status);
        Assert.False(recovered.NetworkAccessBlocked);
        Assert.Equal("1.12.3", recovered.Release!.DisplayVersion);
        AssertNoDownloads();
    }

    private static void AssertRecovery(string message)
    {
        Assert.Contains("10013", message);
        Assert.Contains("Bitdefender", message);
        Assert.Contains("outbound HTTPS", message);
        Assert.Contains(UpdateService.ExePath, message);
    }

    private void AssertNoDownloads() =>
        Assert.Empty(Directory.Exists(_directory) ? Directory.GetFiles(_directory) : Array.Empty<string>());

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request.RequestUri.AbsoluteUri));
        }
    }

    private sealed class CaptureProgress(Action capture) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => capture();
    }

    private sealed class DeniedStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_read) return ValueTask.FromException<int>(Denied());
            _read = true;
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 2)], cancellationToken);
        }
    }
}
