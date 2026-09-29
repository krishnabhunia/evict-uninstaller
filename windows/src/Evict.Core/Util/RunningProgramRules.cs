using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>Which running processes belong to an installed program. Pure logic – unit tested.</summary>
public static class RunningProgramRules
{
    /// <summary>
    /// Folders the program owns: its install folder and the folders of its main executable and icon. Shared or system
    /// folders (a drive root, Program Files, Windows, a user profile, a vendor folder such as "Microsoft") never count –
    /// they would match processes of other programs.
    /// </summary>
    public static List<string> OwnedFolders(InstalledProgram p)
    {
        var folders = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return;
            var f = folder.Trim().Trim('"').TrimEnd('\\', '/');
            if (f.Length < 4 || !RegistryCleanerRules.IsAbsoluteLocal(f)) return;
            if (PathUtil.IsProtectedRoot(f)) return;
            if (!folders.Any(x => PathUtil.IsUnder(f, x)))
            {
                folders.RemoveAll(x => PathUtil.IsUnder(x, f));
                folders.Add(f);
            }
        }

        Add(p.InstallLocation);
        if (IsExe(p.PrimaryExecutable)) Add(PathUtil.ParentPath(p.PrimaryExecutable!.Trim().Trim('"')));
        var (icon, _) = PathUtil.SplitIconPath(p.DisplayIcon);
        if (IsExe(icon)) Add(PathUtil.ParentPath(icon!));
        return folders;
    }

    public static bool BelongsTo(string? imagePath, IReadOnlyList<string> folders) =>
        !string.IsNullOrEmpty(imagePath) && folders.Any(f => PathUtil.IsUnder(imagePath, f));

    private static bool IsExe(string? path) => !string.IsNullOrWhiteSpace(path) && path.Trim().Trim('"').EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Notepad++ (notepad++.exe)" style list line; several processes of one exe are counted once.</summary>
    public static List<string> Summarize(IEnumerable<(string Name, string? WindowTitle)> processes) =>
        processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var title = g.Select(p => p.WindowTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
                var count = g.Count() > 1 ? $" ×{g.Count()}" : "";
                return title != null ? $"{title} ({g.Key}.exe{count})" : $"{g.Key}.exe{count}";
            })
            .ToList();
}
