using System.Text.Json;
using Evict.Core.Models;

namespace Evict.Core.Services;

public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private List<UninstallHistoryEntry> _entries = new();

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
                if (File.Exists(AppPaths.HistoryFile))
                    _entries = JsonSerializer.Deserialize<List<UninstallHistoryEntry>>(File.ReadAllText(AppPaths.HistoryFile), Options) ?? new();
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
        try { File.WriteAllText(AppPaths.HistoryFile, JsonSerializer.Serialize(_entries, Options)); }
        catch { /* ignore */ }
    }
}
