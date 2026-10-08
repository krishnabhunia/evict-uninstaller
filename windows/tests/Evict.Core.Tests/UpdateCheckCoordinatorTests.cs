using System.Net;
using System.Text.Json;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class UpdateCheckCoordinatorTests
{
    private static readonly UpdateCheckResult Current = new(UpdateStatus.UpToDate, new Version(1, 10, 0), null, "Current.");

    [Theory]
    [InlineData(false, "1.10.0")]
    [InlineData(true, "1.11.0-beta.13.32.1")]
    public async Task EachRestartQueriesGitHubEvenWhenSettingsRememberARecentCheck(bool includeBeta, string expectedVersion)
    {
        var saved = JsonSerializer.Serialize(new AppSettings
        {
            CheckForUpdates = true,
            IncludeBetaUpdates = includeBeta,
            LastUpdateCheckUtc = DateTime.UtcNow,
        });
        using var handler = new ReleasesHandler();
        using var client = new HttpClient(handler);
        var service = new UpdateService(client, Path.Combine(Path.GetTempPath(), "EvictStartupUnused-" + Guid.NewGuid().ToString("N")),
            currentVersionLabel: "1.9.0");

        for (var restart = 0; restart < 2; restart++)
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(saved)!;
            SettingsStore.Migrate(settings);
            var checks = new UpdateCheckCoordinator(service.CheckAsync, () => settings.CheckForUpdates,
                () => settings.IncludeBetaUpdates, _ => Task.CompletedTask);
            var result = await checks.CheckOnStartupAsync(CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
            Assert.Equal(expectedVersion, result.Release!.DisplayVersion);
            Assert.False(checks.IsChecking);
        }
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task DisabledAutomaticChecksDoNotDelayOrContactGitHubButManualChecksWork()
    {
        var attempts = 0;
        var checks = new UpdateCheckCoordinator((_, _) => { attempts++; return Task.FromResult(Current); },
            () => false, () => false, _ => throw new InvalidOperationException("No automatic delay expected"));

        Assert.Null(await checks.CheckOnStartupAsync(CancellationToken.None));
        Assert.Same(Current, await checks.CheckNowAsync(CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DisablingChecksDuringStartupDelayPreventsTheRequest()
    {
        var enabled = true;
        var delay = new DelayGate();
        var attempts = 0;
        var checks = new UpdateCheckCoordinator((_, _) => { attempts++; return Task.FromResult(Current); },
            () => enabled, () => false, delay.WaitAsync);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        enabled = false;
        delay.Release.TrySetResult();
        Assert.Null(await startup);
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task SettingsResetDuringDelayUsesTheCurrentObjectAndChannel()
    {
        var settings = new AppSettings { IncludeBetaUpdates = true };
        var delay = new DelayGate();
        var channels = new List<bool>();
        var checks = new UpdateCheckCoordinator((_, beta) => { channels.Add(beta); return Task.FromResult(Current); },
            () => settings.CheckForUpdates, () => settings.IncludeBetaUpdates, delay.WaitAsync);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        settings = new AppSettings();
        delay.Release.TrySetResult();
        Assert.Same(Current, await startup);
        Assert.Equal(new[] { false }, channels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCheckDuringDelayReplacesAutomaticCheckWhetherItIsCompletedOrStillRunning(bool manualStillRunning)
    {
        var delay = new DelayGate();
        var request = new CheckGate();
        var checks = new UpdateCheckCoordinator(request.CheckAsync, () => true, () => false, delay.WaitAsync);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var manual = checks.CheckNowAsync(CancellationToken.None);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!manualStillRunning)
        {
            request.Release.TrySetResult(Current);
            Assert.Same(Current, await manual);
        }

        delay.Release.TrySetResult();
        Assert.Null(await startup);
        if (manualStillRunning)
        {
            request.Release.TrySetResult(Current);
            Assert.Same(Current, await manual);
        }
        Assert.Equal(1, request.Attempts);
        Assert.False(checks.IsChecking);
    }

    [Fact]
    public async Task ManualCheckBeforeStartupReplacesTheAutomaticRequest()
    {
        var attempts = 0;
        var checks = new UpdateCheckCoordinator((_, _) => { attempts++; return Task.FromResult(Current); },
            () => true, () => false, _ => throw new InvalidOperationException("No startup delay expected"));

        Assert.Same(Current, await checks.CheckNowAsync(CancellationToken.None));
        Assert.Null(await checks.CheckOnStartupAsync(CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SimultaneousStartupCallsAndManualCheckShareOneInFlightRequest()
    {
        var request = new CheckGate();
        var states = new List<bool>();
        var checks = new UpdateCheckCoordinator(request.CheckAsync, () => true, () => false, _ => Task.CompletedTask);
        checks.CheckingChanged += states.Add;
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(checks.IsChecking);
        Assert.Null(await checks.CheckOnStartupAsync(CancellationToken.None));
        Assert.Null(await checks.CheckNowAsync(CancellationToken.None));
        request.Release.TrySetResult(Current);
        Assert.Same(Current, await startup);
        Assert.Equal(1, request.Attempts);
        Assert.Equal(new[] { true, false }, states);
        Assert.False(checks.IsChecking);
    }

    [Fact]
    public async Task ShutdownDuringStartupDelayCancelsWithoutARequest()
    {
        using var cancellation = new CancellationTokenSource();
        var delay = new DelayGate();
        var attempts = 0;
        var checks = new UpdateCheckCoordinator((_, _) => { attempts++; return Task.FromResult(Current); },
            () => true, () => false, delay.WaitAsync);
        var startup = checks.CheckOnStartupAsync(cancellation.Token);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.Equal(0, attempts);
        Assert.False(checks.IsChecking);
    }

    [Fact]
    public async Task CancellationReleasesTheRequestGuardAndAllowsAManualRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var request = new CheckGate();
        var states = new List<bool>();
        var checks = new UpdateCheckCoordinator(request.CheckAsync, () => true, () => false, _ => Task.CompletedTask);
        checks.CheckingChanged += states.Add;
        var startup = checks.CheckOnStartupAsync(cancellation.Token);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.False(checks.IsChecking);
        request.Release.TrySetResult(Current);
        Assert.Same(Current, await checks.CheckNowAsync(CancellationToken.None));
        Assert.Equal(2, request.Attempts);
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public async Task ChangingBetaPreferenceDuringARequestSuppressesTheOldChannelResult()
    {
        var includeBeta = true;
        var request = new CheckGate();
        var checks = new UpdateCheckCoordinator(request.CheckAsync, () => true, () => includeBeta, _ => Task.CompletedTask);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        includeBeta = false;
        request.Release.TrySetResult(Current);
        Assert.Null(await startup);
        Assert.False(checks.IsChecking);
    }

    [Fact]
    public async Task DisablingAutomaticChecksDuringARequestSuppressesItsNotification()
    {
        var enabled = true;
        var request = new CheckGate();
        var checks = new UpdateCheckCoordinator(request.CheckAsync, () => enabled, () => false, _ => Task.CompletedTask);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await request.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        enabled = false;
        request.Release.TrySetResult(Current);
        Assert.Null(await startup);
        Assert.False(checks.IsChecking);
    }

    [Fact]
    public async Task FailedRequestReleasesTheGuardForTheNextCheck()
    {
        var attempts = 0;
        var checks = new UpdateCheckCoordinator((_, _) => ++attempts == 1
                ? Task.FromException<UpdateCheckResult>(new HttpRequestException("Fixture offline"))
                : Task.FromResult(Current),
            () => true, () => false, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<HttpRequestException>(() => checks.CheckOnStartupAsync(CancellationToken.None));
        Assert.False(checks.IsChecking);
        Assert.Same(Current, await checks.CheckNowAsync(CancellationToken.None));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CheckingNotificationsAlwaysMatchTheObservableState()
    {
        var checks = new UpdateCheckCoordinator((_, _) => Task.FromResult(Current),
            () => true, () => false, _ => Task.CompletedTask);
        var states = new List<bool>();
        checks.CheckingChanged += value =>
        {
            Assert.Equal(value, checks.IsChecking);
            states.Add(value);
        };

        Assert.Same(Current, await checks.CheckOnStartupAsync(CancellationToken.None));
        Assert.Same(Current, await checks.CheckNowAsync(CancellationToken.None));
        Assert.Equal(new[] { true, false, true, false }, states);
    }

    [Fact]
    public async Task EnablingBetaDuringStartupDelayUsesTheNewPreference()
    {
        var includeBeta = false;
        var delay = new DelayGate();
        var channels = new List<bool>();
        var checks = new UpdateCheckCoordinator((_, beta) => { channels.Add(beta); return Task.FromResult(Current); },
            () => true, () => includeBeta, delay.WaitAsync);
        var startup = checks.CheckOnStartupAsync(CancellationToken.None);
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        includeBeta = true;
        delay.Release.TrySetResult();
        Assert.Same(Current, await startup);
        Assert.Equal(new[] { true }, channels);
    }

    private sealed class DelayGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WaitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            return Release.Task.WaitAsync(ct);
        }
    }

    private sealed class CheckGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UpdateCheckResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }
        public Task<UpdateCheckResult> CheckAsync(CancellationToken ct, bool includeBeta)
        {
            Attempts++;
            Entered.TrySetResult();
            return Release.Task.WaitAsync(ct);
        }
    }

    private sealed class ReleasesHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(UpdateChecker.ReleasesApiUrl, request.RequestUri!.AbsoluteUri);
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    [
                      { "tag_name": "win-v1.10.0", "draft": false, "prerelease": false, "assets": [] },
                      { "tag_name": "win-v1.11.0-beta.13.32.1", "draft": false, "prerelease": true, "assets": [] }
                    ]
                    """),
            });
        }
    }
}
