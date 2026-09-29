using Evict.Core.Models;

namespace Evict.Core.Util;

/// <summary>How a program's own uninstaller ended, judged from its exit code and whether the program is still there.</summary>
public enum UninstallOutcome
{
    Succeeded,
    /// <summary>Removed, but Windows must restart to finish (3010 / 1641).</summary>
    RebootRequired,
    /// <summary>The uninstaller could not be started (missing file, no command, UAC declined).</summary>
    NotStarted,
    /// <summary>Cancelled – in Evict, or in the uninstaller's own window (1602).</summary>
    Cancelled,
    /// <summary>Windows Installer is busy with another installation (1618) – worth waiting and retrying.</summary>
    AnotherInstallInProgress,
    /// <summary>A non-zero exit code and the program is still registered.</summary>
    Failed,
    /// <summary>Exit code 0, yet the entry and the program's files are still there (a cancelled or deferred uninstall).</summary>
    StillInstalled,
}

/// <summary>An alternative way to run a program's uninstaller when the first one failed.</summary>
public sealed record UninstallAlternative(UninstallCommand Command, string Label);

/// <summary>
/// Decisions of the uninstall pipeline when something goes wrong: what counts as a failure, and which other uninstall
/// commands are worth trying. Pure logic – unit tested.
/// </summary>
public static class UninstallRecoveryRules
{
    public static UninstallOutcome Evaluate(UninstallRunResult r, bool stillInstalled)
    {
        if (r.Cancelled) return UninstallOutcome.Cancelled;
        if (!r.Launched) return r.Error?.Contains("UAC", StringComparison.OrdinalIgnoreCase) == true ? UninstallOutcome.Cancelled : UninstallOutcome.NotStarted;
        if (r.ExitCode is 1618) return UninstallOutcome.AnotherInstallInProgress;
        if (r.ExitCode is 1602) return UninstallOutcome.Cancelled;
        if (r.ExitCode is 1605) return UninstallOutcome.Succeeded; // "this product is not installed" – already gone
        if (r.ExitCode is 3010 or 1641) return UninstallOutcome.RebootRequired;
        if (r.RegistryEntryRemoved) return UninstallOutcome.Succeeded;
        if (r.ExitCode is 0) return stillInstalled ? UninstallOutcome.StillInstalled : UninstallOutcome.Succeeded;
        // Non-zero and still registered. Without files left the entry is only an orphan – the scan removes it.
        return stillInstalled ? UninstallOutcome.Failed : UninstallOutcome.Succeeded;
    }

    /// <summary>Only after these may leftovers be scanned (and removed) without asking – the program is gone.</summary>
    public static bool IsSuccess(UninstallOutcome o) => o is UninstallOutcome.Succeeded or UninstallOutcome.RebootRequired;

    /// <summary>One line for the fallback question: "the uninstaller exited with code 1603 and … is still installed".</summary>
    public static string Describe(UninstallOutcome o, UninstallRunResult r, string programName) => o switch
    {
        UninstallOutcome.NotStarted => "the uninstaller could not be started" + (r.Error is { } e ? ": " + e.TrimEnd('.') : ""),
        UninstallOutcome.Cancelled => "the uninstall was cancelled" + (r.ExitCode is 1602 ? " in the uninstaller's window" : ""),
        UninstallOutcome.AnotherInstallInProgress => "another installation is in progress (Windows Installer is busy)",
        UninstallOutcome.Failed => $"the uninstaller failed (exit code {r.ExitCode}{(MsiErrorText(r.ExitCode) is { } t ? " – " + t : "")}) and {programName} is still installed",
        UninstallOutcome.StillInstalled => $"the uninstaller finished, but {programName} is still installed",
        _ => "the uninstall finished",
    };

    public static string? MsiErrorText(int? code) => code switch
    {
        1603 => "fatal error during installation",
        1605 => "product not installed",
        1612 => "the installation source is not available",
        1618 => "another installation is in progress",
        1619 or 1620 => "the installation package could not be opened",
        1639 => "invalid command line",
        1624 => "error applying transforms",
        1722 or 1723 => "a custom action of the package failed",
        5 => "access denied – administrator rights may be needed",
        _ => null,
    };

    /// <summary>
    /// Other ways to run the uninstaller than <paramref name="used"/>, best first: the interactive uninstaller after a
    /// silent attempt (its window shows the real error), Windows Installer by product code, the program's own silent
    /// command. Never the same command twice.
    /// </summary>
    public static List<UninstallAlternative> Alternatives(InstalledProgram p, UninstallCommand? used, Func<string, byte[]?>? readHead = null)
    {
        var result = new List<UninstallAlternative>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (used != null) seen.Add(used.Display);
        void Add(UninstallCommand? cmd, string label)
        {
            if (cmd != null && seen.Add(cmd.Display)) result.Add(new UninstallAlternative(cmd, label));
        }

        Add(UninstallCommandParser.Resolve(p, quiet: false, readHead), "Run the uninstaller with its own window");
        var productCode = UninstallCommandParser.IsGuid(p.MsiProductCode) ? p.MsiProductCode : UninstallCommandParser.IsGuid(p.KeyName) ? p.KeyName : null;
        if (productCode != null) Add(UninstallCommandParser.BuildMsiUninstall(productCode, quiet: false), "Uninstall through Windows Installer (msiexec)");
        Add(UninstallCommandParser.Resolve(p, quiet: true, readHead), "Run the uninstaller silently");
        return result;
    }
}
