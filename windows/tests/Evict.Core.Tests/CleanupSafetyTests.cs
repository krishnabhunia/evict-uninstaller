using System.Diagnostics;
using System.Text;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public class CleanupSafetyTests
{
    [Theory]
    [InlineData(1223)]
    [InlineData(unchecked((int)0x80070005))]
    public void Recycling_failure_keeps_file_and_never_falls_back_to_permanent_deletion(int error)
    {
        using var fixture = new TempFixture();
        var file = Path.Combine(fixture.Root, "keep.txt");
        File.WriteAllText(file, "retain this fixture");
        bool permanentCalled = false;
        var result = LeftoverCleaner.DeletePathDetailed(file, false, new CleanupOptions { SendToRecycleBin = true },
            recycle: _ => error, permanentDelete: (_, _, _) => permanentCalled = true);
        Assert.NotNull(result.Error);
        Assert.False(result.Removed);
        Assert.False(result.Recycled);
        Assert.False(permanentCalled);
        Assert.Equal("retain this fixture", File.ReadAllText(file));
    }

    [Fact]
    public void Recycling_success_requires_the_source_to_be_gone()
    {
        using var fixture = new TempFixture();
        var file = Path.Combine(fixture.Root, "keep.txt");
        File.WriteAllText(file, "still here");
        var result = LeftoverCleaner.DeletePathDetailed(file, false, new CleanupOptions(), recycle: _ => 0);
        Assert.NotNull(result.Error);
        Assert.False(result.Recycled);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Cleanup_outcomes_distinguish_recycling_permanent_deletion_and_absent_items()
    {
        var recycled = FileItem("recycled.txt", 100);
        var permanent = FileItem("permanent.txt", 50);
        var absent = FileItem("absent.txt", 200);
        var cleaner = new LeftoverCleaner((path, _, _) => path switch
        {
            "recycled.txt" => new(null, Removed: true, Recycled: true),
            "permanent.txt" => new(null, Removed: true),
            _ => new(null),
        });
        var result = cleaner.Clean(new[] { recycled, permanent, absent }, new CleanupOptions(), null, CancellationToken.None);
        Assert.Equal(2, result.Removed);
        Assert.Equal(150, result.BytesRemoved);
        Assert.Equal(50, result.BytesReclaimed);
        Assert.Equal(new[] { "recycled.txt" }, result.RecycledPaths);
        Assert.Equal(new[] { recycled, permanent }, result.RemovedItems);
    }

    [Fact]
    public void Hard_directory_deletion_removes_junction_only_and_preserves_external_sentinel()
    {
        using var fixture = new TempFixture();
        var selected = Directory.CreateDirectory(Path.Combine(fixture.Root, "SelectedApp")).FullName;
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "OtherApp")).FullName;
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "other application's data");
        File.WriteAllText(Path.Combine(selected, "own.txt"), "selected application");
        var link = Path.Combine(selected, "SharedData");
        CreateDirectoryLink(link, external);

        LeftoverCleaner.DeleteDirectoryHard(selected, new CleanupOptions { SendToRecycleBin = false, ScheduleLockedForReboot = false });

        Assert.False(Directory.Exists(selected));
        Assert.Equal("other application's data", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Selected_directory_reached_through_a_junction_is_refused()
    {
        using var fixture = new TempFixture();
        var external = Directory.CreateDirectory(Path.Combine(fixture.Root, "OtherApp")).FullName;
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        var link = Path.Combine(fixture.Root, "SelectedLink");
        CreateDirectoryLink(link, external);
        var result = LeftoverCleaner.DeletePathDetailed(link, true,
            new CleanupOptions { SendToRecycleBin = false, ScheduleLockedForReboot = false });
        Assert.NotNull(result.Error);
        Assert.False(result.Removed);
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Metadata_cleanup_failure_does_not_leave_successfully_restored_data_pending_for_undo()
    {
        using var fixture = new TempFixture();
        var source = Path.Combine(fixture.Root, "recycled-fixture.txt");
        var destination = Path.Combine(fixture.Root, "restored-fixture.txt");
        File.WriteAllText(source, "restored contents");
        var restored = new RecycleBinService.RestoreResult();
        RecycleBinService.RestoreItem(source, destination, "fixture-info", restored, File.Move,
            _ => throw new UnauthorizedAccessException("fixture metadata denied"));
        Assert.Equal("restored contents", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
        Assert.Equal(1, restored.Restored);
        Assert.Equal(new[] { destination }, restored.RestoredPaths);
        Assert.Empty(restored.Errors);
        Assert.Single(restored.Warnings);

        var item = FileItem(destination, 10);
        var cleanup = new CleanupResult();
        cleanup.RemovedItems.Add(item);
        cleanup.RecycledPaths.Add(destination);
        var undo = new CleanupUndo((_, _) => Task.FromResult(restored), _ => throw new InvalidOperationException("No registry expected"));
        undo.BeginIfFirst();
        undo.Record(new[] { item }, cleanup, true);
        var outcome = await undo.UndoAsync();
        Assert.True(outcome.Ok);
        Assert.False(undo.CanUndo);
        Assert.Equal(new[] { item }, undo.LastRestoredItems);
        Assert.Contains(outcome.Lines, line => line.Contains("metadata could not be removed"));
    }

    [Fact]
    public void Failed_data_move_is_never_recorded_as_restored()
    {
        var restored = new RecycleBinService.RestoreResult();
        bool metadataDeleted = false;
        Assert.Throws<IOException>(() => RecycleBinService.RestoreItem("source", "destination", "info", restored,
            (_, _) => throw new IOException("fixture move failed"), _ => metadataDeleted = true));
        Assert.Equal(0, restored.Restored);
        Assert.Empty(restored.RestoredPaths);
        Assert.False(metadataDeleted);
    }

    [Fact]
    public async Task Partial_undo_retains_only_failed_files_and_registry_backup_for_retry()
    {
        var first = FileItem(Path.Combine(Path.GetTempPath(), "first-fixture.txt"), 10);
        var second = FileItem(Path.Combine(Path.GetTempPath(), "second-fixture.txt"), 20);
        var registry = new LeftoverItem { Kind = LeftoverKind.RegistryKey, Path = "HKCU\\Software\\Fixture" };
        int filesAttempt = 0, registryAttempt = 0;
        var undo = new CleanupUndo((paths, _) =>
        {
            var requested = paths.ToList();
            var result = new RecycleBinService.RestoreResult { Restored = 1 };
            if (filesAttempt++ == 0)
            {
                Assert.Equal(new[] { first.Path, second.Path }, requested);
                result.RestoredPaths.Add(first.Path);
                result.NotFound.Add(second.Path);
            }
            else
            {
                Assert.Equal(new[] { second.Path }, requested);
                result.RestoredPaths.Add(second.Path);
            }
            return Task.FromResult(result);
        }, _ => Task.FromResult(registryAttempt++ == 0 ? (false, "access denied") : (true, "restored")));
        var cleanup = new CleanupResult { RegistryBackupFile = "fixture-backup.reg" };
        cleanup.RemovedItems.AddRange(new[] { first, second, registry });
        cleanup.RecycledPaths.AddRange(new[] { first.Path, second.Path });
        undo.BeginIfFirst();
        undo.Record(cleanup.RemovedItems, cleanup, true);

        var initial = await undo.UndoAsync();
        Assert.False(initial.Ok);
        Assert.True(undo.CanUndo);
        Assert.Equal("fixture-backup.reg", undo.RegistryBackupFile);
        Assert.Equal(new[] { first }, undo.LastRestoredItems);

        var retry = await undo.UndoAsync();
        Assert.True(retry.Ok);
        Assert.False(undo.CanUndo);
        Assert.Null(undo.RegistryBackupFile);
        Assert.Equal(new[] { second, registry }, undo.LastRestoredItems);
    }

    [Fact]
    public void Undo_records_actual_disposition_instead_of_requested_preference()
    {
        var file = FileItem(Path.Combine(Path.GetTempPath(), "disposition-fixture.txt"), 10);
        var permanent = new CleanupResult();
        permanent.RemovedItems.Add(file);
        var undo = new CleanupUndo();
        undo.Record(new[] { file }, permanent, sentToRecycleBin: true);
        Assert.False(undo.CanUndo);

        var recycled = new CleanupResult();
        recycled.RemovedItems.Add(file);
        recycled.RecycledPaths.Add(file.Path);
        undo.Record(new[] { file }, recycled, sentToRecycleBin: false);
        Assert.True(undo.CanUndo);
    }

    [Fact]
    public async Task Registry_retry_backups_restore_newest_first_and_keep_original_snapshot_until_success()
    {
        var calls = new List<string>();
        bool rejectLatest = true;
        var undo = new CleanupUndo((_, _) => throw new InvalidOperationException("No recycled files expected"), file =>
        {
            calls.Add(file);
            return Task.FromResult((file != "latest.reg" || !rejectLatest, "fixture result"));
        });
        undo.Record(Array.Empty<LeftoverItem>(), new CleanupResult { RegistryBackupFile = "original.reg" }, false);
        undo.Record(Array.Empty<LeftoverItem>(), new CleanupResult { RegistryBackupFile = "latest.reg" }, false);
        Assert.False((await undo.UndoAsync()).Ok);
        Assert.Equal(new[] { "latest.reg" }, calls);
        Assert.Equal(new[] { "original.reg", "latest.reg" }, undo.RegistryBackupFiles);
        rejectLatest = false;
        calls.Clear();
        Assert.True((await undo.UndoAsync()).Ok);
        Assert.Equal(new[] { "latest.reg", "original.reg" }, calls);
        Assert.False(undo.CanUndo);
    }

    private static LeftoverItem FileItem(string path, long size) => new() { Kind = LeftoverKind.File, Path = path, SizeBytes = size };

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // Directory junctions do not require the symbolic-link privilege. Both paths are isolated fixtures.
        var script = $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class TempFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "Evict-cleanup-test-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose()
        {
            if (Directory.Exists(Root)) LeftoverCleaner.DeleteDirectoryHard(Root,
                new CleanupOptions { SendToRecycleBin = false, ScheduleLockedForReboot = false });
        }
    }
}
