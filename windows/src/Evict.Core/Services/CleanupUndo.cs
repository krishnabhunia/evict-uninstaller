using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Remembers what a leftover cleanup removed so it can be put back: files and folders from the Recycle Bin, registry
/// entries from the .reg backup taken before they were deleted. Permanently deleted files, services and scheduled tasks
/// cannot come back.
/// </summary>
public sealed class CleanupUndo
{
    private readonly Dictionary<string, LeftoverItem> _recycledItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string File, List<LeftoverItem> Items)> _backups = new();
    private readonly Func<IEnumerable<string>, DateTime, Task<RecycleBinService.RestoreResult>> _restoreFiles;
    private readonly Func<string, Task<(bool Ok, string Message)>> _restoreRegistry;
    private DateTime _startedUtc;

    public CleanupUndo() : this((paths, started) => Task.Run(() => RecycleBinService.Restore(paths, started)),
        file => RegistryBackupService.RestoreAsync(file)) { }

    internal CleanupUndo(Func<IEnumerable<string>, DateTime, Task<RecycleBinService.RestoreResult>> restoreFiles,
        Func<string, Task<(bool Ok, string Message)>> restoreRegistry)
    {
        _restoreFiles = restoreFiles;
        _restoreRegistry = restoreRegistry;
    }

    public string? RegistryBackupFile => _backups.Count > 0 ? _backups[0].File : null;
    public IReadOnlyList<string> RegistryBackupFiles => _backups.Select(b => b.File).ToList();
    public bool CanUndo => _recycledItems.Count > 0 || _backups.Count > 0;
    /// <summary>Actual item identities restored by the most recent Undo call, for partial-result counters and history.</summary>
    public List<LeftoverItem> LastRestoredItems { get; } = new();
    public bool LastRegistryRestoreSucceeded { get; private set; }

    /// <summary>Call before the first cleanup of a run (the Recycle Bin is searched for items deleted after this).</summary>
    public void BeginIfFirst()
    {
        if (!CanUndo) _startedUtc = DateTime.UtcNow;
    }

    public void Record(IEnumerable<LeftoverItem> items, CleanupResult result, bool sentToRecycleBin)
    {
        // The requested preference cannot establish that a file was recycled. Use the cleaner's actual outcomes.
        foreach (var path in result.RecycledPaths)
        {
            var item = result.RemovedItems.FirstOrDefault(i => i.IsFileSystem && SamePath(i.Path, path));
            if (item != null) _recycledItems[path] = item;
        }
        if (result.RegistryBackupFile is { } backup && !_backups.Any(b => SamePath(b.File, backup)))
            _backups.Add((backup, result.RemovedItems.Where(i => i.IsRegistry).ToList()));
    }

    /// <summary>Puts everything back. Returns whether all of it worked, and one line per result for the user.</summary>
    public async Task<(bool Ok, List<string> Lines)> UndoAsync()
    {
        var lines = new List<string>();
        LastRestoredItems.Clear();
        LastRegistryRestoreSucceeded = false;
        bool ok = true;
        if (_recycledItems.Count > 0)
        {
            try
            {
                var r = await _restoreFiles(_recycledItems.Keys.ToList(), _startedUtc).ConfigureAwait(false);
                lines.Add($"{r.Restored} file/folder item(s) restored from the Recycle Bin.");
                foreach (var path in r.RestoredPaths)
                {
                    var key = _recycledItems.Keys.FirstOrDefault(k => SamePath(k, path));
                    if (key != null && _recycledItems.Remove(key, out var item)) LastRestoredItems.Add(item);
                }
                if (r.NotFound.Count > 0) lines.Add($"{r.NotFound.Count} item(s) were not found in the Recycle Bin; they remain pending for retry.");
                foreach (var e in r.Errors) lines.Add("  " + e);
                foreach (var warning in r.Warnings) lines.Add("  " + warning);
                ok &= _recycledItems.Count == 0 && r.Errors.Count == 0 && r.NotFound.Count == 0;
            }
            catch (Exception ex) { lines.Add("Files: " + ex.Message); ok = false; }
        }
        var registryRestored = new List<LeftoverItem>();
        bool hadRegistry = _backups.Count > 0;
        // A retry can create an additional backup. Restore newest first so the first snapshot wins finally.
        foreach (var backup in _backups.AsEnumerable().Reverse().ToList())
        {
            bool regOk;
            string message;
            try { (regOk, message) = await _restoreRegistry(backup.File).ConfigureAwait(false); }
            catch (Exception ex) { regOk = false; message = ex.Message; }
            lines.Add(regOk ? "Registry entries restored from the backup." : "Registry: " + message);
            ok &= regOk;
            if (!regOk) break; // keep the older snapshots until the newer one can be restored
            registryRestored.AddRange(backup.Items);
            _backups.Remove(backup);
        }
        LastRegistryRestoreSucceeded = hadRegistry && _backups.Count == 0;
        LastRestoredItems.AddRange(registryRestored.Where(i => !_backups.Any(b => b.Items.Contains(i))).Distinct());
        return (ok, lines);
    }

    private static bool SamePath(string left, string right)
    {
        var a = PathUtil.NormalizeForCompare(left);
        var b = PathUtil.NormalizeForCompare(right);
        return a.Length > 0 && b.Length > 0 ? a.Equals(b, StringComparison.OrdinalIgnoreCase)
            : left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }
}
