using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>Finds the installed program that owns a file / process path or matches a name. Pure logic – unit tested.</summary>
public static class ProgramMatcher
{
    /// <summary>
    /// Best program for an executable path: exact primary-executable match, then the program whose install
    /// folder contains the path (deepest folder wins), then a program whose DisplayIcon points at the same exe.
    /// </summary>
    public static InstalledProgram? FindByPath(IEnumerable<InstalledProgram> programs, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = PathUtil.NormalizeForCompare(path);
        var list = programs.ToList();

        var exact = list.FirstOrDefault(x => !string.IsNullOrEmpty(x.PrimaryExecutable) && PathUtil.NormalizeForCompare(x.PrimaryExecutable).Equals(p, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        InstalledProgram? best = null;
        int bestLen = -1;
        foreach (var x in list)
        {
            if (string.IsNullOrEmpty(x.InstallLocation)) continue;
            if (!PathUtil.IsUnder(p, x.InstallLocation)) continue;
            var len = PathUtil.NormalizeForCompare(x.InstallLocation).Length;
            if (len > bestLen) { best = x; bestLen = len; }
        }
        if (best != null) return best;

        var exeName = PathUtil.LeafName(p);
        if (exeName.Length > 4)
        {
            var byIcon = list.FirstOrDefault(x =>
            {
                var (icon, _) = PathUtil.SplitIconPath(x.DisplayIcon);
                return icon.Length > 0 && PathUtil.LeafName(icon).Equals(exeName, StringComparison.OrdinalIgnoreCase) && !ShortcutInspector.IsGenericExeName(exeName);
            });
            if (byIcon != null) return byIcon;
        }
        return null;
    }

    /// <summary>Exact (case-insensitive) display-name match first, then the single program containing the text.</summary>
    public static InstalledProgram? FindByName(IEnumerable<InstalledProgram> programs, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var list = programs.ToList();
        var exact = list.FirstOrDefault(x => x.DisplayName.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var key = NameNormalizer.ToKey(name);
        var byKey = list.Where(x => NameNormalizer.ToKey(x.DisplayName) == key).ToList();
        if (byKey.Count == 1) return byKey[0];
        var contains = list.Where(x => x.DisplayName.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return contains.Count == 1 ? contains[0] : null;
    }
}
