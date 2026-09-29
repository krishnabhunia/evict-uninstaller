using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>What is wrong with a program's uninstall entry (Programs → Uninstall issues, Software Health).</summary>
public enum UninstallIssue
{
    None,
    /// <summary>Uninstaller and install folder are both gone – only the entry is left.</summary>
    Broken,
    /// <summary>The program's files are there but its uninstaller file is missing.</summary>
    UninstallerMissing,
    /// <summary>The entry has no uninstall command at all (and is not a Windows Installer product).</summary>
    NoUninstaller,
    /// <summary>An earlier uninstall through Evict failed and the program is still installed.</summary>
    FailedBefore,
}

/// <summary>Pure classification of uninstall problems. Unit tested with fake file checks.</summary>
public static class UninstallIssueRules
{
    public static UninstallIssue Classify(InstalledProgram p, Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        if (p.IsMsi) return UninstallIssue.None; // msiexec can remove it (or reports why not)
        bool folderKnown = !string.IsNullOrWhiteSpace(p.InstallLocation);
        bool folderMissing = !folderKnown || !dirExists(p.InstallLocation!);
        var cmd = UninstallCommandParser.Parse(p.UninstallString) ?? UninstallCommandParser.Parse(p.QuietUninstallString);
        if (cmd is null) return folderMissing ? UninstallIssue.Broken : UninstallIssue.NoUninstaller;
        bool judged = !UninstallCommandParser.IsMsiExec(cmd)
                      && !cmd.FileName.Contains("rundll32", StringComparison.OrdinalIgnoreCase)
                      && RegistryCleanerRules.IsAbsoluteLocal(cmd.FileName); // network / relative paths are never judged
        if (!judged || fileExists(cmd.FileName)) return UninstallIssue.None;
        return folderMissing ? UninstallIssue.Broken : UninstallIssue.UninstallerMissing;
    }

    /// <summary>
    /// True when the history holds a failed uninstall of this program newer than its install date – it was tried and
    /// is still here.
    /// </summary>
    public static bool FailedBefore(InstalledProgram p, IEnumerable<UninstallHistoryEntry> history) =>
        history.Any(h => !h.Succeeded && h.ProgramName.Equals(p.DisplayName, StringComparison.OrdinalIgnoreCase)
                         && (p.EffectiveInstallDate is null || h.Timestamp >= p.EffectiveInstallDate.Value));

    public static string Describe(UninstallIssue issue) => issue switch
    {
        UninstallIssue.Broken => "Broken entry: install folder and uninstaller are missing",
        UninstallIssue.UninstallerMissing => "Uninstaller missing: the program's files are still there",
        UninstallIssue.NoUninstaller => "No uninstall command registered",
        UninstallIssue.FailedBefore => "An earlier uninstall failed – the program is still installed",
        _ => "",
    };
}
