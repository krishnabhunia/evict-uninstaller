namespace Evict.Core.Util;

/// <summary>
/// Ranking for the Ctrl+K search window (RD1): programs, pages, tools and settings are matched by title first,
/// then by extra keywords, so "unin" finds "Uninstalling" before a tool that only mentions it.
/// </summary>
public static class QuickSearch
{
    /// <summary>0 = no match; higher is better. An empty query matches everything with the lowest score.</summary>
    public static int Score(string? query, string title, string? keywords = null)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return 1;
        if (string.IsNullOrEmpty(title)) return 0;

        // Every word of the query must appear somewhere (title or keywords).
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var haystack = title + " " + (keywords ?? "");
        if (!words.All(w => haystack.Contains(w, StringComparison.OrdinalIgnoreCase))) return 0;

        if (title.Equals(q, StringComparison.OrdinalIgnoreCase)) return 400;
        if (title.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 300;
        if (StartsAWord(title, q)) return 200;
        if (title.Contains(q, StringComparison.OrdinalIgnoreCase)) return 150;
        if (words.All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase))) return 120;
        return 50;
    }

    /// <summary>The best <paramref name="max"/> matches, best first; ties keep the input order.</summary>
    public static IReadOnlyList<T> Top<T>(IEnumerable<T> items, string? query, Func<T, string> title, Func<T, string?>? keywords, int max)
    {
        return items
            .Select((item, index) => (item, index, score: Score(query, title(item), keywords?.Invoke(item))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(Math.Max(0, max))
            .Select(x => x.item)
            .ToList();
    }

    private static bool StartsAWord(string title, string q)
    {
        for (int i = title.IndexOf(q, StringComparison.OrdinalIgnoreCase); i >= 0; i = title.IndexOf(q, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(title[i - 1])) return true;
            if (i + 1 >= title.Length) break;
        }
        return false;
    }
}
