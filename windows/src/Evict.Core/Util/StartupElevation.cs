namespace Evict.Core.Util;

/// <summary>
/// "Start as administrator": when Evict asks Windows for administrator rights at start-up, and how it relaunches itself.
/// Pure logic – unit tested.
/// </summary>
public static class StartupElevation
{
    /// <summary>
    /// Elevate only a start that shows the window: not a start hidden in the notification area (sign-in autostart, Task
    /// Scheduler scan – a UAC prompt nobody expects), not Evict's self-cleanup or "--exit", not the elevated relaunch
    /// itself (--no-elevate), and only for an administrator account – a standard account would run Evict as someone else.
    /// </summary>
    public static bool ShouldElevate(bool setting, bool isElevated, bool canElevateSameUser, CommandLineOptions options) =>
        setting && !isElevated && canElevateSameUser
        && !options.NoElevate && !options.Headless && !options.Exit && options.SelfCleanup is null;

    /// <summary>
    /// Arguments for the elevated copy: the original ones (so "Uninstall with Evict" still opens the right program), plus
    /// --no-elevate (never ask twice) and --wait-pid (take over the single-instance lock once this process has exited).
    /// </summary>
    public static List<string> RelaunchArgs(IReadOnlyList<string> args, int currentPid)
    {
        var result = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var key = args[i].Trim().TrimStart('-', '/').ToLowerInvariant();
            if (key is "no-elevate" or "noelevate") continue;
            if (key is "wait-pid" or "waitpid") { i++; continue; }
            result.Add(args[i]);
        }
        result.Add("--no-elevate");
        result.Add("--wait-pid");
        result.Add(currentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return result;
    }
}
