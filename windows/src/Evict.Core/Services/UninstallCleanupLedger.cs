using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Actual item outcomes across cleanup retries and partial restores; attribution uses the original scanned items.</summary>
public sealed class UninstallCleanupLedger
{
    private readonly Dictionary<LeftoverItem, (long Removed, long Reclaimed)> _removed = new();
    public int RemovedCount => _removed.Count;
    public long BytesRemoved => _removed.Values.Sum(v => v.Removed);
    public long BytesReclaimed => _removed.Values.Sum(v => v.Reclaimed);

    public void Record(CleanupResult result)
    {
        var recycled = result.RecycledPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.RemovedItems)
        {
            var bytes = item.IsFileSystem ? Math.Max(0, item.SizeBytes) : 0;
            _removed[item] = (bytes, recycled.Contains(item.Path) ? 0 : bytes);
        }
    }

    public void Restore(IEnumerable<LeftoverItem> items)
    {
        foreach (var item in items) _removed.Remove(item);
    }

    public (int Count, long BytesRemoved, long BytesReclaimed) ForItems(IEnumerable<LeftoverItem> items)
    {
        var owned = items.Distinct().Where(_removed.ContainsKey).Select(i => _removed[i]).ToList();
        return (owned.Count, owned.Sum(v => v.Removed), owned.Sum(v => v.Reclaimed));
    }
}
