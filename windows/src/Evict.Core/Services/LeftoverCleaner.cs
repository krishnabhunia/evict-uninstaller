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
}

/// <summary>Deletes the items a <see cref="LeftoverScanner"/> found. Each item is independent; failures are collected.</summary>
public sealed class LeftoverCleaner
{
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

        int n = 0;
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            progress?.Report(new ProgressReport($"Removing {item.Path}", 100.0 * n / Math.Max(1, list.Count)));
            string? error = item.Kind switch
            {
                LeftoverKind.Folder => DeletePath(item.Path, isDirectory: true, options),
                LeftoverKind.File or LeftoverKind.Shortcut => DeletePath(item.Path, isDirectory: false, options),
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
                result.Removed++;
                result.BytesReclaimed += item.SizeBytes;
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
    {
        try
        {
            if (isDirectory ? !Directory.Exists(path) : !File.Exists(path)) return null; // already gone

            if (!trustedRoot && isDirectory && PathUtil.IsProtectedRoot(path, checkProtectedNames: false))
                return "Refusing to delete a protected system folder.";

            if (options.SendToRecycleBin)
            {
                int rc = NativeMethods.SendToRecycleBin(path);
                if (rc == 0) return null;
                // 0x71 = DE_SAMEFILE etc.; fall through to a hard delete for anything the shell refused.
                Log.Warn($"Recycle bin refused {path} (0x{rc:X}); trying permanent delete.");
            }

            if (isDirectory) DeleteDirectoryHard(path, options);
            else DeleteFileHard(path, options);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
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

    private static void DeleteDirectoryHard(string path, CleanupOptions options)
    {
        var enumOpts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, AttributesToSkip = 0 };
        var lockedAny = false;
        foreach (var file in Directory.EnumerateFiles(path, "*", enumOpts))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (IOException) when (options.ScheduleLockedForReboot && ElevationHelper.IsElevated)
            {
                lockedAny = true;
                NativeMethods.MoveFileExW(file, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            }
        }
        foreach (var dir in Directory.EnumerateDirectories(path, "*", enumOpts).OrderByDescending(d => d.Length))
        {
            try { new DirectoryInfo(dir).Attributes = FileAttributes.Normal; Directory.Delete(dir, false); } catch { lockedAny = true; }
        }
        try
        {
            new DirectoryInfo(path).Attributes = FileAttributes.Normal;
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) when (lockedAny && ElevationHelper.IsElevated)
        {
            NativeMethods.MoveFileExW(path, null, NativeMethods.MOVEFILE_DELAY_UNTIL_REBOOT);
            throw new IOException("Some files are in use – the folder will be removed at the next restart.");
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
