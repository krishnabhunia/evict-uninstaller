using System.Text.Json;
using Evict.Core.Models;

namespace Evict.Core.Services;

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string? _file;
    private List<UninstallHistoryEntry> _entries = new();

    public HistoryStore() { }
    internal HistoryStore(string file) => _file = file;
    private string FilePath => _file ?? AppPaths.HistoryFile;

    public IReadOnlyList<UninstallHistoryEntry> Entries
    {
        get { lock (_gate) return _entries.OrderByDescending(e => e.Timestamp).ToList(); }
    }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    _entries = JsonSerializer.Deserialize<List<UninstallHistoryEntry>>(File.ReadAllText(FilePath), Options) ?? new();
            }
            catch { _entries = new(); }
        }
    }

    public void Add(UninstallHistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Persist();
        }
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            Persist();
        }
    }

    /// <summary>Updates the same operation after a retry or restore rather than adding another history row.</summary>
    public void Upsert(UninstallHistoryEntry entry)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index < 0) _entries.Add(entry); else _entries[index] = entry;
            Persist();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Persist();
        }
    }

    private void Persist()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, Options)); }
        catch { /* ignore */ }
    }
}
