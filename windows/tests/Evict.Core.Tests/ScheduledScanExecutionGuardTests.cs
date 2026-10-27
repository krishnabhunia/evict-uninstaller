using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class ScheduledScanExecutionGuardTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static ScheduledScanSnapshot Active => new("Daily", 10, 0, Now.AddDays(-3), Now.AddDays(-2));

    [Theory]
    [InlineData("Off")]
    [InlineData("invalid")]
    public async Task DisabledOrInvalidPreferenceNeverQueriesOrScans(string mode)
    {
        var queries = 0;
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active with { Mode = mode },
            (_, _) => { queries++; return Task.FromResult(true); },
            _ => { scans++; return Task.CompletedTask; });
        Assert.False(await guard.TryRunAsync(false));
        Assert.Equal(0, queries);
        Assert.Equal(0, scans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiedTaskNeverScans(bool catchUp)
    {
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active, (_, _) => Task.FromResult(false),
            _ => { scans++; return Task.CompletedTask; }, _ => Task.CompletedTask, () => Now);
        Assert.False(await guard.TryRunAsync(catchUp));
        Assert.Equal(0, scans);
    }

    [Fact]
    public async Task ConfigurationChangedDuringVerificationNeverScans()
    {
        var state = Active;
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => state,
            (_, _) => { state = state with { Hour = 12 }; return Task.FromResult(true); },
            _ => { scans++; return Task.CompletedTask; });
        Assert.False(await guard.TryRunAsync(false));
        Assert.Equal(0, scans);
    }

    [Fact]
    public async Task DisablingDuringCatchUpDelayPreventsScan()
    {
        var state = Active;
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => state, (_, _) => Task.FromResult(true),
            _ => { scans++; return Task.CompletedTask; },
            _ => { state = state with { Mode = "Off" }; return Task.CompletedTask; }, () => Now);
        Assert.False(await guard.TryRunAsync(true));
        Assert.Equal(0, scans);
    }

    [Fact]
    public async Task TaskMustStillMatchAfterCatchUpDelay()
    {
        var queries = 0;
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active,
            (_, _) => Task.FromResult(++queries == 1),
            _ => { scans++; return Task.CompletedTask; }, _ => Task.CompletedTask, () => Now);
        Assert.False(await guard.TryRunAsync(true));
        Assert.Equal(2, queries);
        Assert.Equal(0, scans);
    }

    [Fact]
    public async Task FreshlyAppliedScheduleDoesNotInventACompletedScanOrCatchUpImmediately()
    {
        var state = Active with { AppliedUtc = Now };
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => state, (_, _) => Task.FromResult(true),
            _ => { scans++; return Task.CompletedTask; }, _ => Task.CompletedTask, () => Now);
        Assert.False(await guard.TryRunAsync(true));
        Assert.Equal(Now.AddDays(-2), state.LastRunUtc);
        Assert.Equal(0, scans);
        Assert.False((state with { AppliedUtc = null, LastRunUtc = null }).IsCatchUpDue(Now));
    }

    [Fact]
    public async Task ConcurrentScheduledRequestsRunOnlyOneScan()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active, (_, _) => Task.FromResult(true),
            async _ => { scans++; entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); });
        var first = guard.TryRunAsync(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(await guard.TryRunAsync(false));
        release.SetResult();
        Assert.True(await first);
        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task FailureReleasesTheGuardForAnExplicitLaterAttempt()
    {
        var attempts = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active, (_, _) => Task.FromResult(true),
            _ => ++attempts == 1 ? Task.FromException(new IOException("scan unavailable")) : Task.CompletedTask);
        await Assert.ThrowsAsync<IOException>(() => guard.TryRunAsync(false));
        Assert.True(await guard.TryRunAsync(false));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CancellationDuringDelayDoesNotScan()
    {
        using var cancellation = new CancellationTokenSource();
        var scans = 0;
        var guard = new ScheduledScanExecutionGuard(() => Active, (_, _) => Task.FromResult(true),
            _ => { scans++; return Task.CompletedTask; },
            ct => { cancellation.Cancel(); return Task.FromCanceled(ct); }, () => Now);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => guard.TryRunAsync(true, cancellation.Token));
        Assert.Equal(0, scans);
    }
}
