using System.Text.RegularExpressions;

namespace Evict.Core.Services;

/// <summary>One row of docs/FEATURES.md.</summary>
public sealed record FeatureRow(int SrNo, string Area, string WhatItDoes, string Since, bool OnWindows, bool OnMac)
{
    public string WindowsText => OnWindows ? "Yes" : "Not yet";
    public string MacText => OnMac ? "Yes" : "Not yet";
}

/// <summary>The "What's implemented" filters (chips).</summary>
public enum FeatureFilter { All, NewInThisVersion }

/// <summary>A changelog section shown on the What's new tab.</summary>
public sealed record ChangelogSection(string Version, string Heading, IReadOnlyList<string> Lines)
{
    public string Text => string.Join(Environment.NewLine, Lines);
}

/// <summary>
/// Parsing for the Info window's embedded documents: docs/FEATURES.md (feature table) and windows/CHANGELOG.md
/// (this version's section). Pure string logic, so it is unit tested; the WPF window only displays the result.
/// </summary>
public static class InfoContent
{
    public static readonly string[] FeatureHeader = ["SrNo.", "Area", "What it does", "Since", "Windows", "Mac"];

    /// <summary>Rows of the single feature table; malformed rows are skipped so the window never fails to open.</summary>
    public static IReadOnlyList<FeatureRow> ParseFeatures(string? markdown)
    {
        var rows = new List<FeatureRow>();
        if (string.IsNullOrWhiteSpace(markdown)) return rows;
        bool inTable = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|') || !line.EndsWith('|') || line.Length < 2)
            {
                if (inTable) break;
                continue;
            }
            var cells = line[1..^1].Split('|').Select(c => c.Trim()).ToArray();
            if (!inTable)
            {
                inTable = cells.SequenceEqual(FeatureHeader);
                continue;
            }
            if (cells.All(c => Regex.IsMatch(c, @"\A:?-{3,}:?\z"))) continue;
            if (cells.Length != FeatureHeader.Length || !int.TryParse(cells[0], out var number) || UpdateVersion.Parse(cells[3]) is null) continue;
            rows.Add(new FeatureRow(number, cells[1], cells[2], cells[3], cells[4] == "Yes", cells[5] == "Yes"));
        }
        return rows;
    }

    /// <summary>Search (area or description, any order of words) plus a chip filter; "New in x.y.z" uses <paramref name="coreVersion"/>.</summary>
    public static IReadOnlyList<FeatureRow> FilterFeatures(IEnumerable<FeatureRow> rows, string? query, FeatureFilter filter,
        string coreVersion, bool? mac = null)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var core = UpdateVersion.Parse(coreVersion);
        return rows.Where(row =>
                (mac is null || (mac.Value ? row.OnMac : row.OnWindows)) &&
                (filter != FeatureFilter.NewInThisVersion || (core is not null && UpdateVersion.Parse(row.Since) is { } since && since.CompareTo(core) == 0)) &&
                words.All(word => row.Area.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                                  row.WhatItDoes.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// The CHANGELOG section of <paramref name="coreVersion"/> ("## 4.2.0 — 2026-11-05"), without the generator's
    /// HTML comment and Markdown emphasis; bullets become "•". Null when the version has no section.
    /// </summary>
    public static ChangelogSection? ChangelogFor(string? markdown, string coreVersion)
    {
        if (string.IsNullOrWhiteSpace(markdown) || string.IsNullOrWhiteSpace(coreVersion)) return null;
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var heading = new Regex(@"\A##\s+" + Regex.Escape(coreVersion.Trim()) + @"(?=\s|\z)(?<rest>.*)\z", RegexOptions.CultureInvariant);
        for (int i = 0; i < lines.Length; i++)
        {
            var match = heading.Match(lines[i].Trim());
            if (!match.Success) continue;
            var body = new List<string>();
            for (int j = i + 1; j < lines.Length && !lines[j].StartsWith("## ", StringComparison.Ordinal); j++)
            {
                var text = lines[j].TrimEnd();
                if (Regex.IsMatch(text.Trim(), @"\A<!--.*-->\z")) continue;
                text = text.Replace("**", "").Replace("`", "");
                text = Regex.Replace(text, @"\A(\s*)[-*]\s+", "$1• ");
                text = Regex.Replace(text, @"\A###\s+", "");
                if (text.Length == 0 && (body.Count == 0 || body[^1].Length == 0)) continue;
                body.Add(text);
            }
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            return new ChangelogSection(coreVersion.Trim(), lines[i].Trim()[3..].Trim(), body);
        }
        return null;
    }
}
