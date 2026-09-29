using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>
/// Remembers what a leftover cleanup removed so it can be put back: files and folders from the Recycle Bin, registry
/// entries from the .reg backup taken before they were deleted. Permanently deleted files, services and scheduled tasks
/// cannot come back.
/// </summary>
public sealed class CleanupUndo
{
    private readonly List<string> _recycledPaths = new();
    private DateTime _startedUtc;

    public string? RegistryBackupFile { get; private set; }
    public bool CanUndo => _recycledPaths.Count > 0 || RegistryBackupFile != null;

    /// <summary>Call before the first cleanup of a run (the Recycle Bin is searched for items deleted after this).</summary>
    public void BeginIfFirst()
    {
        if (!CanUndo) _startedUtc = DateTime.UtcNow;
    }

    public void Record(IEnumerable<LeftoverItem> items, CleanupResult result, bool sentToRecycleBin)
    {
        var failed = result.Errors.Select(e => e.Item).ToHashSet();
        if (sentToRecycleBin)
            _recycledPaths.AddRange(items.Where(i => i.Kind is LeftoverKind.File or LeftoverKind.Folder or LeftoverKind.Shortcut && !failed.Contains(i)).Select(i => i.Path));
        // Several cleanups in one run (a retry) each write a backup; the first one holds the most entries.
        RegistryBackupFile ??= result.RegistryBackupFile;
    }

    /// <summary>Puts everything back. Returns whether all of it worked, and one line per result for the user.</summary>
    public async Task<(bool Ok, List<string> Lines)> UndoAsync()
    {
        var lines = new List<string>();
        bool ok = true;
        if (_recycledPaths.Count > 0)
        {
            var paths = _recycledPaths.ToList();
            var r = await Task.Run(() => RecycleBinService.Restore(paths, _startedUtc)).ConfigureAwait(false);
            lines.Add($"{r.Restored} file/folder item(s) restored from the Recycle Bin.");
            if (r.NotFound.Count > 0) lines.Add($"{r.NotFound.Count} item(s) were not in the Recycle Bin (deleted permanently, or already restored).");
            foreach (var e in r.Errors) { lines.Add("  " + e); ok = false; }
            _recycledPaths.Clear();
        }
        if (RegistryBackupFile != null)
        {
            var (regOk, message) = await RegistryBackupService.RestoreAsync(RegistryBackupFile).ConfigureAwait(false);
            lines.Add(regOk ? "Registry entries restored from the backup." : "Registry: " + message);
            ok &= regOk;
            RegistryBackupFile = null;
        }
        return (ok, lines);
    }
}
