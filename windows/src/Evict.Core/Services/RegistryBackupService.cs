using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>A .reg file written before registry entries were deleted.</summary>
public sealed record RegistryBackupInfo(string FilePath, DateTime Created, string Label, long SizeBytes)
{
    public bool TouchesLocalMachine { get; init; }
}

public sealed class RegistryBackupResult
{
    /// <summary>The .reg file, or null when none of the items existed any more (nothing to back up).</summary>
    public string? FilePath { get; set; }
    /// <summary>Items that could not be exported – callers must not delete these.</summary>
    public Dictionary<LeftoverItem, string> Failed { get; } = new();
    public int KeysExported { get; set; }
    public int ValuesExported { get; set; }
}

/// <summary>
/// Exports registry keys/values to a regedit-compatible .reg file before Evict deletes them, and restores such a
/// file with <c>reg import</c>. Backups live in %LocalAppData%\Evict\registry-backups.
/// </summary>
public static class RegistryBackupService
{
    /// <summary>A key tree bigger than this is not backed up (and therefore not deleted).</summary>
    private const int MaxValuesPerItem = 100_000;
    private const int MaxDepth = 48;

    public static string BackupDir => Path.Combine(AppPaths.DataRoot, "registry-backups");

    public static RegistryBackupResult Backup(IReadOnlyList<LeftoverItem> items, string label)
    {
        var result = new RegistryBackupResult();
        var blocks = new List<RegKeyBlock>();
        foreach (var item in items.Where(i => i.IsRegistry))
        {
            if (item.Hive is null || string.IsNullOrEmpty(item.SubKey)) { result.Failed[item] = "Registry location missing."; continue; }
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(item.Hive.Value, item.RegView);
                using var key = baseKey.OpenSubKey(item.SubKey);
                if (key is null) continue; // already gone – nothing to delete, nothing to back up
                var itemBlocks = new List<RegKeyBlock>();
                if (item.Kind == LeftoverKind.RegistryKey)
                {
                    int values = 0;
                    ExportTree(key, item.Hive.Value, item.RegView, item.SubKey, 0, itemBlocks, ref values);
                    result.KeysExported += itemBlocks.Count;
                    result.ValuesExported += values;
                }
                else
                {
                    if (item.ValueName is null) { result.Failed[item] = "Value name missing."; continue; }
                    var raw = NativeMethods.ReadRawRegistryValue(key, item.ValueName);
                    if (raw is null) continue; // value already gone
                    itemBlocks.Add(new RegKeyBlock(RegFileFormat.KeyPath(item.Hive.Value, item.RegView, item.SubKey),
                        new[] { new RegRawValue(item.ValueName, raw.Value.Type, raw.Value.Data) }) { View = ExplicitView(item.RegView) });
                    result.ValuesExported++;
                }
                blocks.AddRange(itemBlocks);
            }
            catch (Exception ex)
            {
                result.Failed[item] = ex is System.Security.SecurityException or UnauthorizedAccessException
                    ? "Access denied while backing it up."
                    : ex.Message;
            }
        }

        if (blocks.Count == 0) return result;
        Directory.CreateDirectory(BackupDir);
        var path = Path.Combine(BackupDir, RegFileFormat.BackupFileName(DateTime.Now, label));
        if (File.Exists(path)) path = Path.Combine(BackupDir, Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N")[..4] + ".reg");
        File.WriteAllBytes(path, RegFileFormat.Encode(RegFileFormat.BuildDocument(blocks)));
        result.FilePath = path;
        Log.Info($"Registry backup: {result.KeysExported} key(s), {result.ValuesExported} value(s) → {path}");
        return result;
    }

    private static void ExportTree(RegistryKey key, RegistryHive hive, RegistryView view, string subKey, int depth, List<RegKeyBlock> blocks, ref int valueCount)
    {
        if (depth > MaxDepth) throw new InvalidOperationException("The key is nested too deeply to back up safely.");
        var values = new List<RegRawValue>();
        foreach (var name in key.GetValueNames())
        {
            var raw = NativeMethods.ReadRawRegistryValue(key, name);
            if (raw is null) continue;
            values.Add(new RegRawValue(name, raw.Value.Type, raw.Value.Data));
            if (++valueCount > MaxValuesPerItem) throw new InvalidOperationException("The key is too large to back up safely.");
        }
        blocks.Add(new RegKeyBlock(RegFileFormat.KeyPath(hive, view, subKey), values) { View = ExplicitView(view) });
        foreach (var child in key.GetSubKeyNames())
        {
            using var sub = key.OpenSubKey(child);
            if (sub is null) continue;
            ExportTree(sub, hive, view, RegistryPaths.Join(subKey, child), depth + 1, blocks, ref valueCount);
        }
    }

    /// <summary>All backups, newest first.</summary>
    public static IReadOnlyList<RegistryBackupInfo> List()
    {
        var list = new List<RegistryBackupInfo>();
        try
        {
            var dir = BackupDir;
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.reg"))
            {
                var fi = new FileInfo(f);
                var parsed = RegFileFormat.ParseBackupFileName(fi.Name);
                bool hklm = false;
                try { hklm = File.ReadAllText(f).Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase); } catch { /* unreadable – treat as HKCU */ }
                list.Add(new RegistryBackupInfo(f, parsed?.When ?? fi.CreationTime, parsed?.Label ?? Path.GetFileNameWithoutExtension(f), fi.Length) { TouchesLocalMachine = hklm });
            }
        }
        catch (Exception ex) { Log.Warn("Listing registry backups failed: " + ex.Message); }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    /// <summary>Puts everything in the backup back with <c>reg import</c>.</summary>
    public static async Task<(bool Ok, string Message)> RestoreAsync(string file, CancellationToken ct = default)
    {
        if (!File.Exists(file)) return (false, "The backup file no longer exists.");
        bool hklm;
        IReadOnlyList<RegistryRestoreDocument> documents;
        try
        {
            var content = File.ReadAllText(file);
            hklm = content.Contains("[HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase)
                || content.Contains("[HKEY_USERS", StringComparison.OrdinalIgnoreCase);
            documents = RegFileFormat.RestoreDocuments(content);
        }
        catch (Exception ex) { return (false, "Could not read the backup: " + ex.Message); }
        if (hklm && !ElevationHelper.IsElevated)
            return (false, "This backup contains machine-wide or other-user entries. Restart Evict as administrator to restore it.");
        try
        {
            var reg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");
            foreach (var document in documents)
            {
                // Import each logical path through its original registry view. This preserves shared-key exceptions.
                var temporary = Path.Combine(Path.GetTempPath(), "Evict-registry-restore-" + Guid.NewGuid().ToString("N") + ".reg");
                try
                {
                    await File.WriteAllBytesAsync(temporary, RegFileFormat.Encode(document.Document), ct).ConfigureAwait(false);
                    var r = await ProcessRunner.RunCapturedAsync(reg, ImportArguments(temporary, document.View), ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                    if (!r.Success)
                    {
                        var text = (r.StdErr + " " + r.StdOut).Trim().Replace("ERROR: ", "");
                        return (false, $"reg import failed ({r.ExitCode}){(text.Length > 0 ? ": " + text : ".")}");
                    }
                }
                finally { try { File.Delete(temporary); } catch { /* temporary import document only */ } }
            }
            Log.Info("Registry backup restored: " + file);
            return (true, "The registry entries were restored using their recorded registry views.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    internal static string ImportArguments(string file, RegistryView view) => $"import \"{file}\"" +
        (view switch { RegistryView.Registry32 => " /reg:32", RegistryView.Registry64 => " /reg:64", _ => "" });

    private static RegistryView ExplicitView(RegistryView view) => view == RegistryView.Default
        ? Environment.Is64BitProcess ? RegistryView.Registry64 : RegistryView.Registry32
        : view;

    public static void Delete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) { Log.Warn("Deleting registry backup failed: " + ex.Message); }
    }
}
