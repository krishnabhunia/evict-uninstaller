using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SoftwareUpdateHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Evict-update-history-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "software-update-history.json");
    private DateTimeOffset _now = new(2026, 10, 19, 11, 33, 0, TimeSpan.Zero);
    private SoftwareUpdateHistoryStore Store() => new(JournalPath, () => _now);
    private static UpgradablePackage Package(string id = "Fixture.App", string? source = "winget") =>
        new() { Name = "Fixture " + id, Id = id, Source = source, InstalledVersion = "1.0", AvailableVersion = "2.0" };
    private static SoftwareUpgradeResult Success => new(SoftwareUpdateOutcome.Updated, "Installed.", 0);
    public SoftwareUpdateHistoryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ResultsAndMetadataSurviveRestart()
    {
        var store = Store();
        var id = store.Begin(Package());
        store.AppendOutput(id, "Installer output");
        _now += TimeSpan.FromSeconds(42);
        store.Complete(id, Success);
        var reloaded = Store();
        var record = Assert.Single(reloaded.Snapshot());
        Assert.Equal(id, record.Id);
        Assert.Equal("Fixture.App", record.PackageId);
        Assert.Equal("winget", record.Source);
        Assert.Equal("1.0", record.PreviousVersion);
        Assert.Equal("2.0", record.TargetVersion);
        Assert.Equal(SoftwareUpdateOutcome.Updated, record.Outcome);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(42), record.FinishedAtUtc - record.StartedAtUtc);
        Assert.Contains("Installer output", record.Output);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void RollingWindowRemovesRecordsAtTheExact168HourBoundary(int secondsPastBoundary, bool retained)
    {
        var store = Store();
        var id = store.Begin(Package());
        store.Complete(id, Success);
        _now += TimeSpan.FromDays(7) + TimeSpan.FromSeconds(secondsPastBoundary);
        store.PruneExpired();
        Assert.Equal(retained ? 1 : 0, store.Snapshot().Count);
        Assert.Equal(retained ? 1 : 0, Store().Snapshot().Count);
    }

    [Fact]
    public void RetentionUsesAbsoluteTimeAcrossTimezoneOffsets()
    {
        var store = Store();
        var id = store.Begin(Package());
        store.Complete(id, Success);
        _now = _now.AddDays(7).ToOffset(TimeSpan.FromHours(5.5));
        store.PruneExpired();
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void CleanupOnLoadRemovesExpiredRecordsFromThePhysicalFile()
    {
        var store = Store();
        store.Complete(store.Begin(Package()), Success);
        _now += TimeSpan.FromDays(8);
        Assert.Empty(Store().Snapshot());
        using var json = JsonDocument.Parse(File.ReadAllText(JournalPath));
        Assert.Equal(0, json.RootElement.GetProperty("Attempts").GetArrayLength());
    }

    [Fact]
    public void ActiveUpdatesAreNotPrunedAndRetentionStartsAtCompletion()
    {
        var store = Store();
        var id = store.Begin(Package());
        _now += TimeSpan.FromDays(8);
        store.PruneExpired();
        Assert.Equal(SoftwareUpdateOutcome.InProgress, Assert.Single(store.Snapshot()).Outcome);
        store.Complete(id, Success);
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void UnfinishedAttemptsRecoverOnceAsInterruptedWithoutExtendingOnEveryRestart()
    {
        var first = Store();
        var id = first.Begin(Package());
        first.Load();
        Assert.Equal(SoftwareUpdateOutcome.InProgress, Assert.Single(first.Snapshot()).Outcome);
        _now += TimeSpan.FromMinutes(5);
        var second = Store();
        var recovered = Assert.Single(second.Snapshot());
        Assert.Equal(id, recovered.Id);
        Assert.Equal(SoftwareUpdateOutcome.Interrupted, recovered.Outcome);
        Assert.Contains("unknown", recovered.Message);
        var detected = recovered.FinishedAtUtc;
        _now += TimeSpan.FromHours(2);
        Assert.Equal(detected, Assert.Single(Store().Snapshot()).FinishedAtUtc);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":2,\"Attempts\":[]}")]
    [InlineData("{\"SchemaVersion\":1,\"Attempts\":null}")]
    public void InvalidHistoryIsReportedAndNeverOverwrittenByNewAttempts(string original)
    {
        File.WriteAllText(JournalPath, original);
        var store = Store();
        store.Load();
        Assert.NotNull(store.LoadError);
        var id = store.Begin(Package());
        store.Complete(id, Success);
        Assert.True(store.HasUnsavedChanges);
        Assert.False(store.ClearCompleted());
        Assert.Equal(original, File.ReadAllText(JournalPath));
        Assert.Equal(id, Assert.Single(store.Snapshot()).Id);
    }

    [Fact]
    public void RetryingAReadMergesRecoveredHistoryWithPendingInMemoryAttempts()
    {
        var seed = Store();
        seed.Complete(seed.Begin(Package("Older.App")), Success);
        var valid = File.ReadAllText(JournalPath);
        File.WriteAllText(JournalPath, "broken");
        var store = Store();
        var pending = store.Begin(Package("Newer.App"));
        Assert.NotNull(store.LoadError);
        File.WriteAllText(JournalPath, valid);
        Assert.True(store.RetrySave());
        Assert.Null(store.LoadError);
        Assert.Equal(2, store.Snapshot().Count);
        Assert.Equal(SoftwareUpdateOutcome.InProgress, store.Snapshot().Single(r => r.Id == pending).Outcome);
    }

    [Fact]
    public void WriteFailuresKeepResultsInMemoryAndRetryPersistsThem()
    {
        bool fail = true;
        var store = new SoftwareUpdateHistoryStore(JournalPath, () => _now, (path, json) =>
        {
            if (fail) throw new IOException("fixture disk unavailable");
            File.WriteAllText(path, json);
        });
        var id = store.Begin(Package());
        store.Complete(id, Success);
        Assert.NotNull(store.SaveError);
        Assert.True(store.HasUnsavedChanges);
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(store.Snapshot()).Outcome);
        fail = false;
        Assert.True(store.RetrySave());
        Assert.Null(store.SaveError);
        Assert.False(store.HasUnsavedChanges);
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(Store().Snapshot()).Outcome);
    }

    [Fact]
    public void AtomicReplacementDoesNotTruncateALockedWindowsJournal()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store();
        var id = store.Begin(Package());
        var before = File.ReadAllText(JournalPath);
        using (var locked = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Complete(id, Success);
            Assert.NotNull(store.SaveError);
            Assert.Equal(before, File.ReadAllText(JournalPath));
        }
        Assert.True(store.RetrySave());
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(Store().Snapshot()).Outcome);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ClearRemovesOnlyCompletedAttemptsAndPersistsTheActiveOne()
    {
        var store = Store();
        store.Complete(store.Begin(Package("Done.App")), Success);
        var active = store.Begin(Package("Active.App"));
        Assert.True(store.ClearCompleted());
        Assert.Equal(active, Assert.Single(store.Snapshot()).Id);
        using var json = JsonDocument.Parse(File.ReadAllText(JournalPath));
        Assert.Equal(1, json.RootElement.GetProperty("Attempts").GetArrayLength());
    }

    [Fact]
    public void FailedClearRestoresExistingRecords()
    {
        bool fail = false;
        var store = new SoftwareUpdateHistoryStore(JournalPath, () => _now, (path, json) =>
        {
            if (fail) throw new IOException("fixture blocked");
            File.WriteAllText(path, json);
        });
        var id = store.Begin(Package());
        store.Complete(id, Success);
        fail = true;
        Assert.False(store.ClearCompleted());
        Assert.Equal(id, Assert.Single(store.Snapshot()).Id);
        Assert.Equal(id, Assert.Single(Store().Snapshot()).Id);
    }

    [Fact]
    public void OutputIsBoundedAndSnapshotsCannotMutateStoredLogs()
    {
        var store = Store();
        var id = store.Begin(Package());
        for (int n = 0; n < 200; n++) store.AppendOutput(id, n + new string('x', 1500));
        var first = Assert.Single(store.Snapshot());
        Assert.Equal(SoftwareUpdateHistoryStore.MaximumOutputLines, first.Output.Count);
        Assert.All(first.Output, line => Assert.True(line.Length <= SoftwareUpdateHistoryStore.MaximumLineLength));
        ((string[])first.Output)[0] = "changed outside store";
        Assert.DoesNotContain("changed outside store", Assert.Single(store.Snapshot()).Output);
        store.Complete(id, Success);
        Assert.Equal(80, Assert.Single(Store().Snapshot()).Output.Count);
    }

    [Fact]
    public async Task ParallelWritersDoNotLoseIndependentAttempts()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(n => Task.Run(() =>
        {
            var id = store.Begin(Package("App." + n));
            store.AppendOutput(id, "line " + n);
            store.Complete(id, Success);
        })));
        var records = Store().Snapshot();
        Assert.Equal(24, records.Count);
        Assert.Equal(24, records.Select(r => r.Id).Distinct().Count());
        Assert.All(records, r => Assert.Equal(SoftwareUpdateOutcome.Updated, r.Outcome));
    }

    [Fact]
    public async Task CancellationRecordsQueuedItemsAndDoesNotClaimARunningInstallerWasRolledBack()
    {
        var store = Store();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int launched = 0;
        var session = new SoftwareUpdateSession(store, async (_, ct, _, _) =>
        {
            Interlocked.Increment(ref launched);
            started.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, ct);
            return Success;
        });
        using var cts = new CancellationTokenSource();
        var pending = session.RunAsync(new[] { Package("Running.App"), Package("Queued.App") }, 1, false, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var results = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, launched);
        Assert.Equal(SoftwareUpdateOutcome.Interrupted, results.Single(r => r.PackageId == "Running.App").Outcome);
        Assert.Equal(SoftwareUpdateOutcome.Cancelled, results.Single(r => r.PackageId == "Queued.App").Outcome);
        Assert.All(results, r => Assert.NotNull(r.FinishedAtUtc));
        Assert.DoesNotContain(store.Snapshot(), r => r.Outcome == SoftwareUpdateOutcome.InProgress);
    }

    [Fact]
    public async Task CancellationBeforeLaunchCreatesCancelledAttemptsWithoutExecutingInstallers()
    {
        int launched = 0;
        var session = new SoftwareUpdateSession(Store(), (_, _, _, _) => { launched++; return Task.FromResult(Success); });
        var results = await session.RunAsync(new[] { Package(), Package("Other.App") }, 2, false, new CancellationToken(true));
        Assert.Equal(0, launched);
        Assert.All(results, r => Assert.Equal(SoftwareUpdateOutcome.Cancelled, r.Outcome));
    }

    [Fact]
    public async Task SessionHonorsParallelLimitAndIsolatesExceptionsAndObservers()
    {
        var store = Store();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = new();
        int active = 0, maximum = 0;
        var session = new SoftwareUpdateSession(store, async (package, _, output, unknown) =>
        {
            Assert.True(unknown);
            lock (gate) { active++; maximum = Math.Max(maximum, active); if (active == 2) ready.TrySetResult(true); }
            try
            {
                await release.Task;
                output?.Invoke("installer fixture");
                if (package.Id == "Throw.App") throw new IOException("fixture result missing");
                return Success;
            }
            finally { lock (gate) active--; }
        });
        var run = session.RunAsync(new[] { Package("Throw.App"), Package("Second.App"), Package("Third.App") },
            2, true, CancellationToken.None, _ => throw new Exception("observer"), (_, _) => throw new Exception("output observer"));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.TrySetResult(true);
        var results = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, maximum);
        Assert.Equal(1, results.Count(r => r.Outcome == SoftwareUpdateOutcome.Interrupted));
        Assert.Equal(2, results.Count(r => r.Outcome == SoftwareUpdateOutcome.Updated));
        Assert.All(results, r => Assert.Contains("installer fixture", r.Output));
    }

    [Theory]
    [InlineData(0, SoftwareUpdateOutcome.Updated)]
    [InlineData(3010, SoftwareUpdateOutcome.RestartRequired)]
    [InlineData(1641, SoftwareUpdateOutcome.RestartRequired)]
    [InlineData(unchecked((int)0x8A150109), SoftwareUpdateOutcome.RestartRequired)]
    [InlineData(unchecked((int)0x8A15010B), SoftwareUpdateOutcome.RestartRequired)]
    [InlineData(unchecked((int)0x8A15010A), SoftwareUpdateOutcome.Failed)]
    [InlineData(1602, SoftwareUpdateOutcome.Cancelled)]
    [InlineData(unchecked((int)0x8A15010C), SoftwareUpdateOutcome.Cancelled)]
    [InlineData(unchecked((int)0x80072EE7), SoftwareUpdateOutcome.Failed)]
    public async Task WingetRecordsStructuredExitCodesInsteadOfInferringSuccessFromOutput(int exitCode, SoftwareUpdateOutcome outcome)
    {
        var winget = new WingetService(() => "fixture.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = exitCode, StdOut = "Successfully installed might occur in earlier output." }));
        var result = await winget.UpgradeWithResultAsync(Package(), CancellationToken.None);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(outcome is SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.RestartRequired, result.Ok);
    }

    [Fact]
    public async Task TimeoutWithASuccessExitCodeIsStillUnconfirmed()
    {
        var winget = new WingetService(() => "fixture.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = 0, TimedOut = true, StdOut = "Successfully installed" }));
        var result = await winget.UpgradeWithResultAsync(Package(), CancellationToken.None);
        Assert.Equal(SoftwareUpdateOutcome.Interrupted, result.Outcome);
        Assert.False(result.Ok);
    }

    [Fact]
    public void UpdateJournalIsHistoryRatherThanOrdinarySettings()
    {
        Assert.Equal(SelfCleanupParts.History, SelfCleanupService.ClassifyDataEntry("software-update-history.json"));
        Assert.Equal(SelfCleanupParts.History, SelfCleanupService.ClassifyDataEntry("SOFTWARE-UPDATE-HISTORY.JSON"));
    }

    [Fact]
    public void StructurallyIncompleteAttemptsDoNotBecomeInventedInterruptedUpdates()
    {
        var incomplete = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Attempts = new[] { new { Id = Guid.NewGuid(), Name = "Fixture", PackageId = "Fixture.App", StartedAtUtc = _now } },
        });
        File.WriteAllText(JournalPath, incomplete);
        var store = Store();
        Assert.Empty(store.Snapshot());
        Assert.NotNull(store.LoadError);
        store.Complete(store.Begin(Package()), Success);
        Assert.Equal(incomplete, File.ReadAllText(JournalPath));
    }

    [Fact]
    public void ARepeatedCallbackCannotReplaceAnAlreadyFinalResult()
    {
        var store = Store();
        var id = store.Begin(Package());
        store.Complete(id, Success);
        store.Complete(id, new(SoftwareUpdateOutcome.Failed, "late callback"));
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(store.Snapshot()).Outcome);
    }

    [Fact]
    public async Task ClearingACompletedRecordDuringProgressReportingDoesNotBreakTheSession()
    {
        var store = Store();
        var session = new SoftwareUpdateSession(store, (_, _, _, _) => Task.FromResult(Success));
        var results = await session.RunAsync(new[] { Package() }, 1, false, CancellationToken.None,
            record => { if (record.Outcome != SoftwareUpdateOutcome.InProgress) store.ClearCompleted(); });
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(results).Outcome);
        Assert.Empty(store.Snapshot());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
