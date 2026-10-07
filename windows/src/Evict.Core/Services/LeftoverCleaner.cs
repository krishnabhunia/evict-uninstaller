using System.ServiceProcess;
using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public sealed class CleanupOptions
{
    public bool SendToRecycleBin { get; set; } = true;
    /// <summary>When a file is locked, schedule its deletion for the next reboot (requires admin).</summary>
    public bool ScheduleLockedForReboot { get; set; } = true;
    /// <summary>Export every registry key/value to a .reg file before deleting it (items that cannot be exported are kept).</summary>
    public bool BackupRegistry { get; set; } = true;
    /// <summary>Label in the backup's file name, e.g. "Uninstall Foo".</summary>
    public string BackupLabel { get; set; } = "Evict cleanup";
}

/// <summary>Deletes the items a <see cref="LeftoverScanner"/> found. Each item is independent; failures are collected.</summary>
public sealed class LeftoverCleaner
{
    private readonly Func<string, bool, CleanupOptions, PathDeletionResult> _deletePath;

    public LeftoverCleaner() : this((path, directory, options) => DeletePathDetailed(path, directory, options)) { }

    internal LeftoverCleaner(Func<string, bool, CleanupOptions, PathDeletionResult> deletePath) => _deletePath = deletePath;

    public Task<CleanupResult> CleanAsync(IEnumerable<LeftoverItem> items, CleanupOptions options, IProgress<ProgressReport>? progress, CancellationToken ct) =>
        Task.Run(() => Clean(items, options, progress, ct), ct);

    public CleanupResult Clean(IEnumerable<LeftoverItem> items, CleanupOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var result = new CleanupResult();
        var list = items
            // Services and tasks first (they may lock files), then files, then folders (deepest first), then registry.
            .OrderBy(i => i.Kind switch
            {
                LeftoverKind.Service => 0,
                LeftoverKind.ScheduledTask => 1,
                LeftoverKind.StartupEntry => 2,
                LeftoverKind.Shortcut => 3,
                LeftoverKind.File => 4,
                LeftoverKind.Folder => 5,
                _ => 6,
            })
            .ThenByDescending(i => i.Kind == LeftoverKind.Folder ? PathUtil.Depth(i.Path) : 0)
            .ToList();

        // Registry: back up first. Anything that cannot be backed up is not deleted.
        var notBackedUp = new Dictionary<LeftoverItem, string>();
        var registryItems = list.Where(i => i.IsRegistry).ToList();
        if (options.BackupRegistry && registryItems.Count > 0)
        {
            progress?.Report(new ProgressReport("Backing up registry entries…", 0));
            try
            {
                var backup = RegistryBackupService.Backup(registryItems, options.BackupLabel);
                result.RegistryBackupFile = backup.FilePath;
                foreach (var (item, error) in backup.Failed) notBackedUp[item] = error;
            }
            catch (Exception ex)
            {
                Log.Warn("Registry backup failed: " + ex.Message);
                foreach (var item in registryItems) notBackedUp[item] = ex.Message;
            }
        }

        int n = 0;
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            progress?.Report(new ProgressReport($"Removing {item.Path}", 100.0 * n / Math.Max(1, list.Count)));
            PathDeletionResult? pathResult = null;
            if (item.IsFileSystem)
                pathResult = _deletePath(item.Path, item.Kind == LeftoverKind.Folder, options);
            string? error = notBackedUp.TryGetValue(item, out var backupError)
                ? "Not deleted – it could not be backed up first: " + backupError
                : item.Kind switch
            {
                LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut => pathResult!.Value.Error,
                LeftoverKind.RegistryKey => DeleteRegistryKey(item),
                LeftoverKind.RegistryValue or LeftoverKind.StartupEntry => DeleteRegistryValue(item),
                LeftoverKind.Service => DeleteService(item),
                LeftoverKind.ScheduledTask => DeleteScheduledTask(item),
                _ => "Unknown item type",
            };
            // Registry: confirm the key/value is really gone (virtualisation, ACLs or a running program can recreate it).
            if (error is null && item.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.StartupEntry)
            {
                if (RegistryItemExists(item)) error = "Still present after deletion (a running program or permissions restored it).";
                else result.RegistryVerified++;
            }
            if (error is null)
            {
                if (pathResult is { Removed: false }) continue; // already absent; no bytes were removed
                result.Removed++;
                result.RemovedItems.Add(item);
                long bytes = item.IsFileSystem ? Math.Max(0, item.SizeBytes) : 0;
                result.BytesRemoved += bytes;
                if (pathResult is { Recycled: true }) result.RecycledPaths.Add(item.Path);
                else result.BytesReclaimed += bytes;
                Log.Info($"Removed [{item.Kind}] {item.Path}");
            }
            else
            {
                result.Failed++;
                result.Errors.Add((item, error));
                Log.Warn($"Failed [{item.Kind}] {item.Path}: {error}");
            }
        }
        return result;
    }

    // ───────────────────────────── files ─────────────────────────────

    /// <param name="trustedRoot">The caller has already verified the path lies inside a folder it owns the cleanup of
    /// (e.g. Windows\Temp) – skips the generic protected-folder refusal that guards uninstall leftovers.</param>
    public static string? DeletePath(string path, bool isDirectory, CleanupOptions options, bool trustedRoot = false)
        => DeletePathDetailed(path, isDirectory, options, trustedRoot).Error;

    internal readonly record struct PathDeletionResult(string? Error, bool Removed = false, bool Recycled = false);

    // Dependencies can be replaced by tests so failed recycling never exercises real shell deletion.
    internal static PathDeletionResult DeletePathDetailed(string path, bool isDirectory, CleanupOptions options, bool trustedRoot = false,
        Func<string, int>? recycle = null, Action<string, bool, CleanupOptions>? permanentDelete = null)
    {
        try
        {
            if (!PathUtil.TryCanonicalizeAbsolute(path, out var canonical)) return new("Refusing an invalid or ambiguous path.");
            path = canonical;
            if (isDirectory ? !Directory.Exists(path) : !File.Exists(path)) return new(null); // already gone
            if (PathUtil.HasReparsePoint(path)) return new("Refusing a path containing a symbolic link or junction.");

            if (!trustedRoot && isDirectory && PathUtil.IsProtectedRoot(path, checkProtectedNames: false))
                return new("Refusing to delete a protected system folder.");

            if (options.SendToRecycleBin)
            {
                int rc = (recycle ?? NativeMethods.SendToRecycleBin)(path);
                if (rc != 0) return new($"Could not send this item to the Recycle Bin (0x{rc:X8}). It was not permanently deleted.");
                if (isDirectory ? Directory.Exists(path) : File.Exists(path))
                    return new("The item is still present after the Recycle Bin operation.");
                return new(null, Removed: true, Recycled: true);
            }

            if (permanentDelete != null) permanentDelete(path, isDirectory, options);
            else if (isDirectory) DeleteDirectoryHard(path, options);
            else DeleteFileHard(path, options);
            return new(null, Removed: true);
        }
        catch (Exception ex)
        {
            return new(ex.Message);
        }
    }

    private static void DeleteFileHard(string path, CleanupOptions options)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException) when (options.ScheduleLockedForReboot && ElevationHelper.IsElevated)
        {
            if (!NativeMethods.MoveFileExW(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT))
                throw;
            throw new IOException("File is in use – it will be deleted at the next restart.");
        }
    }

    internal static void DeleteDirectoryHard(string path, CleanupOptions options)
    {
        path = Path.GetFullPath(path);
        if (PathUtil.HasReparsePoint(path)) throw new IOException("Refusing a directory reached through a symbolic link or junction.");
        var lockedAny = false;
        DeleteDirectoryContents(path, path, options, ref lockedAny);
        if (lockedAny) throw new IOException("Some files are in use - their removal is scheduled for the next restart.");
    }

    private static void DeleteDirectoryContents(string directory, string root, CleanupOptions options, ref bool lockedAny)
    {
        if (!PathUtil.IsUnder(directory, root) || PathUtil.HasReparsePoint(directory))
            throw new IOException("Refusing to leave the selected directory or follow a junction.");
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = 0 };
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", enumOpts))
        {
            if (!PathUtil.IsUnder(entry, root)) throw new IOException("Refusing to leave the selected directory.");
            var attributes = File.GetAttributes(entry);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            // Delete the link itself without traversing it or changing its target's attributes.
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory) Directory.Delete(entry, recursive: false);
                else File.Delete(entry);
                continue;
            }
            if (isDirectory)
            {
                DeleteDirectoryContents(entry, root, options, ref lockedAny);
                continue;
            }
            if (PathUtil.HasReparsePoint(entry)) throw new IOException("The path changed to a symbolic link or junction.");
            try
            {
                File.SetAttributes(entry, FileAttributes.Normal);
                File.Delete(entry);
            }
            catch (IOException) when (options.ScheduleLockedForReboot && ElevationHelper.IsElevated)
            {
                if (!NativeMethods.MoveFileExW(entry, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT)) throw;
                lockedAny = true;
            }
        }
        if (PathUtil.HasReparsePoint(directory)) throw new IOException("The directory changed to a symbolic link or junction.");
        try
        {
            new DirectoryInfo(directory).Attributes = FileAttributes.Normal;
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException) when (lockedAny && options.ScheduleLockedForReboot && ElevationHelper.IsElevated)
        {
            if (!NativeMethods.MoveFileExW(directory, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT)) throw;
        }
    }

    // ───────────────────────────── registry ─────────────────────────────

    private static string? DeleteRegistryKey(LeftoverItem item)
    {
        if (item.Hive is null || string.IsNullOrEmpty(item.SubKey)) return "Registry location missing.";
        var (parent, leaf) = RegistryPaths.Split(item.SubKey);
        if (leaf.Length == 0) return "Refusing to delete a hive root.";
        // Extra guard: never delete the Uninstall root, SOFTWARE root, Classes, Microsoft.
        if (parent.Length == 0 || leaf.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || leaf.Equals("Classes", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Uninstall", StringComparison.OrdinalIgnoreCase) || leaf.Equals("CurrentVersion", StringComparison.OrdinalIgnoreCase))
            return "Refusing to delete a protected registry key.";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(item.Hive.Value, item.RegView);
            using var parentKey = baseKey.OpenSubKey(parent, writable: true);
            if (parentKey is null) return null; // parent gone → nothing to do
            parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            return null;
        }
        catch (Exception ex) { return FriendlyRegistryError(ex, item); }
    }

    /// <summary>True when the registry key (or value) described by the item still exists.</summary>
    public static bool RegistryItemExists(LeftoverItem item)
    {
        if (item.Hive is null || string.IsNullOrEmpty(item.SubKey)) return false;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(item.Hive.Value, item.RegView);
            using var key = baseKey.OpenSubKey(item.SubKey);
            if (key is null) return false;
            if (item.Kind == LeftoverKind.RegistryKey) return true;
            return item.ValueName != null && key.GetValueNames().Contains(item.ValueName, StringComparer.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string FriendlyRegistryError(Exception ex, LeftoverItem item) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException
            ? (item.Hive == RegistryHive.CurrentUser ? "Access denied (the key is protected)." : "Administrator rights are required to change this part of the registry – restart Evict as administrator.")
            : ex.Message;

    private static string? DeleteRegistryValue(LeftoverItem item)
    {
        if (item.Hive is null || string.IsNullOrEmpty(item.SubKey) || item.ValueName is null) return "Registry location missing.";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(item.Hive.Value, item.RegView);
            using var key = baseKey.OpenSubKey(item.SubKey, writable: true);
            if (key is null) return null;
            key.DeleteValue(item.ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) { return FriendlyRegistryError(ex, item); }
    }

    // ───────────────────────────── services / tasks ─────────────────────────────

    private static string? DeleteService(LeftoverItem item)
    {
        if (string.IsNullOrEmpty(item.ServiceName)) return "Service name missing.";
        if (!ElevationHelper.IsElevated) return "Administrator rights are required to remove a service.";
        try
        {
            try
            {
                using var sc = new ServiceController(item.ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
            }
            catch (InvalidOperationException) { /* not installed / already gone */ }

            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var res = ProcessRunner.RunCapturedAsync(Path.Combine(sys, "sc.exe"), $"delete \"{item.ServiceName}\"", CancellationToken.None, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (res.ExitCode == 0 || res.ExitCode == 1060 /* does not exist */) return null;
            if (res.ExitCode == 1072) return null; // marked for deletion – will vanish on reboot
            return $"sc delete returned {res.ExitCode}: {res.StdOut.Trim()}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? DeleteScheduledTask(LeftoverItem item)
    {
        if (string.IsNullOrEmpty(item.TaskName)) return "Task name missing.";
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var res = ProcessRunner.RunCapturedAsync(Path.Combine(sys, "schtasks.exe"), $"/Delete /TN \"{item.TaskName.TrimStart('\\')}\" /F", CancellationToken.None, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return res.ExitCode == 0 ? null : $"schtasks returned {res.ExitCode}: {(res.StdErr + res.StdOut).Trim()}";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
