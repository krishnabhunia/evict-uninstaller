using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>Which running processes belong to an installed program. Pure logic – unit tested.</summary>
public static class RunningProgramRules
{
    /// <summary>A conservative process scope, built from a fresh inventory rather than a folder-name guess.</summary>
    public sealed record ProcessScope(IReadOnlyList<string> Folders, IReadOnlyList<string> Executables)
    {
        public bool Contains(string? imagePath) => SafeLocalPath(imagePath, false, out var image)
            && (Folders.Any(f => PathUtil.IsUnder(image, f)) || Executables.Contains(image, StringComparer.OrdinalIgnoreCase));
    }

    public static ProcessScope CreateScope(InstalledProgram? target, string? selectedPath, IReadOnlyList<InstalledProgram> installed)
    {
        var folders = new List<string>();
        var exes = new List<string>();
        var explicitOwners = target == null && PathUtil.TryCanonicalizeAbsolute(selectedPath, out var selected)
            ? installed.Where(p => IsExe(selectedPath)
                ? ClaimedExecutables(p).Any(exe => SameExecutable(exe, selected))
                : PathUtil.TryCanonicalizeAbsolute(p.InstallLocation, out var root) && string.Equals(root, selected, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<InstalledProgram>();
        bool IsTarget(InstalledProgram p) => target != null && p.Scope == target.Scope
            && string.Equals(p.KeyName, target.KeyName, StringComparison.OrdinalIgnoreCase);
        var others = installed.Where(p => !IsTarget(p) && !(explicitOwners.Count == 1 && ReferenceEquals(explicitOwners[0], p))).ToList();

        void AddFolder(string? path)
        {
            if (!SafeLocalPath(path, true, out var folder)) return;
            // A parent or child registration can represent a surviving product in a shared vendor folder.
            if (others.SelectMany(ClaimedFolders).Any(root => PathUtil.IsUnder(folder, root) || PathUtil.IsUnder(root, folder))) return;
            if (!folders.Any(root => PathUtil.IsUnder(folder, root)))
            {
                folders.RemoveAll(root => PathUtil.IsUnder(root, folder));
                folders.Add(folder);
            }
        }

        void AddExactExecutable(string? path)
        {
            if (!IsExe(path) || !SafeLocalPath(path, false, out var exe)) return;
            if (others.Any(p => ClaimedExecutables(p).Any(other => SameExecutable(other, exe)))) return;
            if (!exes.Contains(exe, StringComparer.OrdinalIgnoreCase)) exes.Add(exe);
        }

        if (target != null)
        {
            AddFolder(target.InstallLocation);
            var (icon, _) = PathUtil.SplitIconPath(target.DisplayIcon);
            // PrimaryExecutable can be guessed from the first exe in a shared folder. An explicit icon path
            // is stronger evidence and grants permission for that executable alone, never all of its siblings.
            AddExactExecutable(icon);
        }
        else if (IsExe(selectedPath)) AddExactExecutable(selectedPath);
        else AddFolder(selectedPath);
        return new ProcessScope(folders, exes);
    }

    private static IEnumerable<string> ClaimedExecutables(InstalledProgram p)
    {
        if (IsExe(p.PrimaryExecutable) && PathUtil.TryCanonicalizeAbsolute(p.PrimaryExecutable, out var exe)) yield return exe;
        var (icon, _) = PathUtil.SplitIconPath(p.DisplayIcon);
        if (IsExe(icon) && PathUtil.TryCanonicalizeAbsolute(icon, out var iconExe)) yield return iconExe;
    }

    private static IEnumerable<string> ClaimedFolders(InstalledProgram p)
    {
        if (PathUtil.TryCanonicalizeAbsolute(p.InstallLocation, out var folder)) yield return folder;
        foreach (var exe in ClaimedExecutables(p))
        {
            var parent = PathUtil.ParentPath(exe);
            if (parent != null && PathUtil.Depth(parent) > 1) yield return parent;
        }
    }

    private static bool SafeLocalPath(string? path, bool folder, out string canonical)
    {
        canonical = "";
        return RegistryCleanerRules.IsAbsoluteLocal(path) && PathUtil.TryCanonicalizeAbsolute(path, out canonical)
            && !PathUtil.IsProtectedRoot(canonical, checkProtectedNames: folder) && !PathUtil.HasReparsePoint(canonical);
    }

    /// <summary>An unqueryable image never confirms a PID's identity.</summary>
    public static bool SameExecutable(string? current, string? expected) =>
        SafeLocalPath(current, false, out var actual) && SafeLocalPath(expected, false, out var original)
        && string.Equals(actual, original, StringComparison.OrdinalIgnoreCase);

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
