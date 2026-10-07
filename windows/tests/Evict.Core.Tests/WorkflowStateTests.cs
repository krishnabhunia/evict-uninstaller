using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class WorkflowStateTests
{
    private static InstalledProgram Program(bool hasUninstaller = false) => new()
    {
        Id = "test", DisplayName = "Test", KeyName = "Test", Scope = RegistryScope.User,
        RegistryPath = @"HKCU\Software\Test", UninstallString = hasUninstaller ? "uninstall.exe" : null,
    };

    [Fact]
    public void Missing_uninstaller_never_means_completed_without_verified_removal()
    {
        var job = new UninstallJob { Program = Program() };
        UninstallOrchestrator.Complete(job);
        Assert.Equal(JobStatus.CompletedWithWarnings, job.Status);
        Assert.False(UninstallOrchestrator.CanAutoClean(job));
        job.ForceRemoval = true;
        UninstallOrchestrator.Complete(job);
        Assert.Equal(JobStatus.CompletedWithWarnings, job.Status);
        job.ForceRemovalVerified = true;
        UninstallOrchestrator.Complete(job);
        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.False(UninstallOrchestrator.CanAutoClean(job));
    }

    [Theory]
    [InlineData(UninstallOutcome.Succeeded, true)]
    [InlineData(UninstallOutcome.Failed, false)]
    [InlineData(UninstallOutcome.StillInstalled, false)]
    [InlineData(UninstallOutcome.RebootRequired, false)]
    public void Automatic_cleanup_requires_a_successful_normal_uninstaller(UninstallOutcome outcome, bool allowed)
    {
        var job = new UninstallJob { Program = Program(hasUninstaller: true), Outcome = outcome };
        Assert.Equal(allowed, UninstallOrchestrator.CanAutoClean(job));
        job.ForceRemoval = true;
        Assert.False(UninstallOrchestrator.CanAutoClean(job));
    }

    private static LeftoverItem FileItem(string path, long bytes) => new()
    {
        Kind = LeftoverKind.File, Path = path, SizeBytes = bytes,
    };

    [Fact]
    public void Cleanup_retries_and_partial_restores_keep_actual_per_program_totals()
    {
        var a = FileItem("a", 100);
        var retryA = FileItem("retry-a", 200);
        var b = FileItem("b", 300);
        var first = new CleanupResult();
        first.RemovedItems.AddRange(new[] { a, b });
        first.RecycledPaths.Add(a.Path);
        var ledger = new UninstallCleanupLedger();
        ledger.Record(first);
        Assert.Equal((1, 100L, 0L), ledger.ForItems(new[] { a, retryA }));
        Assert.Equal((1, 300L, 300L), ledger.ForItems(new[] { b }));

        var retry = new CleanupResult();
        retry.RemovedItems.Add(retryA);
        retry.RecycledPaths.Add(retryA.Path);
        ledger.Record(retry);
        ledger.Record(first); // repeated reporting must not duplicate counts
        Assert.Equal(3, ledger.RemovedCount);
        Assert.Equal((2, 300L, 0L), ledger.ForItems(new[] { a, retryA }));
        ledger.Restore(new[] { a });
        Assert.Equal((1, 200L, 0L), ledger.ForItems(new[] { a, retryA }));
        Assert.Equal((1, 300L, 300L), ledger.ForItems(new[] { b }));
        Assert.Equal(500L, ledger.BytesRemoved);
        Assert.Equal(300L, ledger.BytesReclaimed);
    }

    [Fact]
    public void History_retry_and_undo_replace_one_persisted_operation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "evict-history-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "history.json");
        try
        {
            var id = Guid.NewGuid();
            var when = new DateTime(2026, 10, 7, 12, 0, 0);
            var store = new HistoryStore(file);
            store.Upsert(new UninstallHistoryEntry { Id = id, Timestamp = when, ProgramName = "Test", LeftoversRemoved = 1, BytesRemoved = 100 });
            store.Upsert(new UninstallHistoryEntry { Id = id, Timestamp = when, ProgramName = "Test", LeftoversRemoved = 2, BytesRemoved = 200 });
            Assert.Single(store.Entries);
            Assert.Equal(2, store.Entries[0].LeftoversRemoved);
            store.Upsert(new UninstallHistoryEntry { Id = id, Timestamp = when, ProgramName = "Test", LeftoversRemoved = 0, BytesRemoved = 0 });
            var reloaded = new HistoryStore(file);
            reloaded.Load();
            var entry = Assert.Single(reloaded.Entries);
            Assert.Equal(id, entry.Id);
            Assert.Equal(when, entry.Timestamp);
            Assert.Equal(0, entry.LeftoversRemoved);
            Assert.Equal(0L, entry.BytesRemoved);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
