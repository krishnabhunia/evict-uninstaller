using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class StartupSequenceTests
{
    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void SplashOnlyForInteractiveStartup(bool hidden, bool cleanup, bool exit, bool expected) =>
        Assert.Equal(expected, StartupSequence.ShouldShowSplash(hidden, cleanup, exit));

    [Fact]
    public async Task RequiredStagesRunInOrderAndOnlyThenComplete()
    {
        var order = new List<string>();
        var reports = new List<StartupProgress>();
        var steps = new[] { "Settings", "History", "Inventory", "Window", "First frame" }
            .Select(name => new StartupStep(name, _ => { order.Add(name); return Task.CompletedTask; })).ToArray();
        await StartupSequence.RunAsync(steps, reports.Add, CancellationToken.None);
        Assert.Equal(steps.Select(s => s.Name), order);
        Assert.Equal(10, reports.Count);
        Assert.Single(reports.Where(p => p.IsComplete));
        Assert.True(reports.Last().IsComplete);
        Assert.Equal("5 of 5 stages complete", reports.Last().Summary);
    }

    [Fact]
    public async Task SlowOperationCannotBeReportedAsCompleteEarly()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new List<StartupProgress>();
        var run = StartupSequence.RunAsync(new[] { new StartupStep("Slow", _ => release.Task) }, reports.Add, CancellationToken.None);
        Assert.False(run.IsCompleted);
        Assert.Single(reports);
        Assert.False(reports[0].IsComplete);
        release.SetResult();
        await run;
        Assert.True(reports.Last().IsComplete);
    }

    [Fact]
    public async Task FailureStopsLaterStagesAndPreservesCause()
    {
        var later = false;
        var reports = new List<StartupProgress>();
        var cause = new IOException("test failure");
        var steps = new[]
        {
            new StartupStep("History", _ => Task.FromException(cause)),
            new StartupStep("Reveal", _ => { later = true; return Task.CompletedTask; })
        };
        var error = await Assert.ThrowsAsync<StartupStageException>(() => StartupSequence.RunAsync(steps, reports.Add, CancellationToken.None));
        Assert.Equal("History", error.Stage);
        Assert.Same(cause, error.InnerException);
        Assert.False(later);
        Assert.DoesNotContain(reports, p => p.IsComplete);
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotRunOrReportAnything()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ran = false;
        var reports = new List<StartupProgress>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupSequence.RunAsync(
            new[] { new StartupStep("Settings", _ => { ran = true; return Task.CompletedTask; }) }, reports.Add, cts.Token));
        Assert.False(ran);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task CancellationDuringStepDoesNotAdvanceReadiness()
    {
        using var cts = new CancellationTokenSource();
        var reports = new List<StartupProgress>();
        var later = false;
        var steps = new[]
        {
            new StartupStep("Settings", _ => { cts.Cancel(); return Task.CompletedTask; }),
            new StartupStep("Reveal", _ => { later = true; return Task.CompletedTask; })
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StartupSequence.RunAsync(steps, reports.Add, cts.Token));
        Assert.False(later);
        Assert.DoesNotContain(reports, p => p.IsComplete);
    }

    [Fact]
    public async Task EmptyPlanCannotClaimReadiness() => await Assert.ThrowsAsync<ArgumentException>(() =>
        StartupSequence.RunAsync(Array.Empty<StartupStep>(), _ => { }, CancellationToken.None));
}
