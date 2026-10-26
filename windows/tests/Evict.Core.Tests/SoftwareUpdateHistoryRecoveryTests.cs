using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SoftwareUpdateHistoryRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "evict-history-recovery-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "history.json");
    private DateTime _now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private SoftwareUpdateHistoryStore Store() => new(FilePath, () => _now);
    public SoftwareUpdateHistoryRecoveryTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { try { Directory.Delete(_directory, true); } catch { } }

    private SoftwareUpdateHistoryEntry Attempt(SoftwareUpdateOutcome outcome = SoftwareUpdateOutcome.Updated) => new()
    {
        Name = "Git", PackageId = "Git.Git", Source = "winget", FromVersion = "1.0", ToVersion = "2.0",
        StartedUtc = _now.AddMinutes(-2), FinishedUtc = outcome is SoftwareUpdateOutcome.Running or SoftwareUpdateOutcome.Waiting ? null : _now,
        Outcome = outcome, Output = new() { "installer output" },
    };

    [Fact]
    public void RestartRecoversRunningAndQueuedAttemptsAndDoesNotExtendTheirRetentionAgain()
    {
        var store = Store();
        store.Add(Attempt(SoftwareUpdateOutcome.Running));
        store.Add(Attempt(SoftwareUpdateOutcome.Waiting));
        var recovered = Store();
        recovered.Load();
        Assert.Contains(recovered.Entries, e => e.Outcome == SoftwareUpdateOutcome.Interrupted);
        Assert.Contains(recovered.Entries, e => e.Outcome == SoftwareUpdateOutcome.Cancelled);
        Assert.All(recovered.Entries, e => Assert.Equal(_now, e.FinishedUtc));
        _now = _now.AddHours(1);
        var next = Store();
        next.Load();
        Assert.All(next.Entries, e => Assert.Equal(_now.AddHours(-1), e.FinishedUtc));
    }

    [Fact]
    public void ActiveAttemptsSurviveRetentionAndClear()
    {
        var store = Store();
        var active = Attempt(SoftwareUpdateOutcome.Running);
        active.StartedUtc = _now.AddDays(-20);
        store.Add(active);
        store.Add(Attempt());
        Assert.False(store.PruneNow());
        store.Clear();
        Assert.Equal(active.Id, Assert.Single(store.Entries).Id);
    }

    [Fact]
    public void RetentionUsesCompletionAndUtcInsteadOfLocalMidnight()
    {
        var entry = Attempt();
        entry.StartedUtc = _now.AddDays(-9);
        entry.FinishedUtc = _now.AddDays(-7);
        Assert.True(SoftwareUpdateHistory.IsKept(entry, _now));
        entry.FinishedUtc = _now.AddDays(-7).AddTicks(-1);
        Assert.False(SoftwareUpdateHistory.IsKept(entry, _now));
        entry.FinishedUtc = _now.AddHours(-167);
        Assert.True(SoftwareUpdateHistory.IsKept(entry, _now.ToLocalTime()));
    }

    [Theory]
    [InlineData("{ invalid json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":99,\"Entries\":[]}")]
    [InlineData("{\"Entries\":[null]}")]
    public void InvalidFilesStayIntactAndNewRecordsRemainAvailableInMemory(string contents)
    {
        File.WriteAllText(FilePath, contents);
        var store = Store();
        store.Load();
        Assert.NotNull(store.LoadError);
        var entry = Attempt();
        store.Add(entry);
        store.Clear();
        Assert.Equal(entry.Id, Assert.Single(store.Entries).Id);
        Assert.Equal(contents, File.ReadAllText(FilePath));
        store.RetryPersistence();
        Assert.NotNull(store.LoadError);
        Assert.Equal(contents, File.ReadAllText(FilePath));
    }

    [Fact]
    public void RetryMergesRecoveredFileWithPendingRecords()
    {
        var diskEntry = Attempt();
        diskEntry.Name = "Existing";
        var disk = Store();
        disk.Add(diskEntry);
        string good = File.ReadAllText(FilePath);
        File.WriteAllText(FilePath, "bad");
        var pending = Store();
        pending.Load();
        var newEntry = Attempt();
        newEntry.Name = "Pending";
        pending.Add(newEntry);
        File.WriteAllText(FilePath, good);
        pending.RetryPersistence();
        Assert.Null(pending.PersistenceError);
        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(2, reloaded.Entries.Count);
        Assert.Contains(reloaded.Entries, e => e.Id == diskEntry.Id);
        Assert.Contains(reloaded.Entries, e => e.Id == newEntry.Id);
    }

    [Fact]
    public void FailedClearKeepsRecordsAndAtomicWriteLeavesOldFileIntact()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.Delete is enforced by Windows.
        var store = Store();
        store.Add(Attempt());
        string original = File.ReadAllText(FilePath);
        using (var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Clear();
            Assert.Single(store.Entries);
            Assert.NotNull(store.SaveError);
            Assert.Equal(original, File.ReadAllText(FilePath));
        }
        store.RetryPersistence();
        Assert.Null(store.PersistenceError);
        Assert.Single(store.Entries);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void FailedWriteRetainsPendingAttemptsUntilRetry()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store();
        store.Add(Attempt());
        var pending = Attempt(SoftwareUpdateOutcome.Failed);
        using (var held = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Add(pending);
            Assert.NotNull(store.SaveError);
            Assert.Equal(2, store.Entries.Count);
        }
        store.RetryPersistence();
        var reloaded = Store();
        reloaded.Load();
        Assert.Null(store.SaveError);
        Assert.Equal(2, reloaded.Entries.Count);
    }

    [Fact]
    public async Task ParallelCompletionsAndClearingDoNotLoseAnActiveAttempt()
    {
        var store = Store();
        var active = Attempt(SoftwareUpdateOutcome.Running);
        store.Add(active);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.Add(Attempt()))));
        Assert.Equal(13, store.Entries.Count);
        store.Clear();
        active.Outcome = SoftwareUpdateOutcome.Updated;
        active.FinishedUtc = _now;
        store.Add(active);
        Assert.Equal(active.Id, Assert.Single(store.Entries).Id);
        var reloaded = Store();
        reloaded.Load();
        Assert.Equal(SoftwareUpdateOutcome.Updated, Assert.Single(reloaded.Entries).Outcome);
    }

    [Fact]
    public void EntriesAreIndependentSnapshots()
    {
        var store = Store();
        var entry = Attempt();
        store.Add(entry);
        entry.Output.Clear();
        store.Entries[0].Output.Clear();
        Assert.Equal("installer output", Assert.Single(store.Entries[0].Output));
    }

    [Theory]
    [InlineData(1, "Successfully installed")]
    [InlineData(1, "Restart your PC to finish installation.")]
    [InlineData(1, "0x8A150109")]
    public async Task AFailedExitCodeCannotBeTurnedIntoSuccessByOutput(int exitCode, string output)
    {
        var service = new WingetService(() => "fake.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = exitCode, StdOut = output }));
        var result = await service.UpgradeWithResultAsync(new UpgradablePackage { Id = "Git.Git", Name = "Git" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.True(result.OutcomeKnown);
    }

    [Fact]
    public async Task TimeoutIsUnconfirmedEvenWithASuccessExitCode()
    {
        var service = new WingetService(() => "fake.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = 0, TimedOut = true, StdOut = "Successfully installed" }));
        var result = await service.UpgradeWithResultAsync(new UpgradablePackage { Id = "Git.Git", Name = "Git" }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.False(result.OutcomeKnown);
    }
}
