namespace Evict.Core.Util;

/// <summary>What the start-up splash is doing (design SP1).</summary>
public enum StartupStep { Starting, WaitingForAdministrator, Settings, History, Programs, Window }

/// <summary>
/// Design SP1: when the splash shows, what it says, and how long Evict waits before opening the window anyway.
/// </summary>
public static class StartupSplashRules
{
    /// <summary>After this the splash offers "Open now".</summary>
    public static readonly TimeSpan OfferOpenNowAfter = TimeSpan.FromSeconds(8);

    /// <summary>After this the window opens without waiting any longer.</summary>
    public static readonly TimeSpan OpenAnywayAfter = TimeSpan.FromSeconds(20);

    /// <summary>The steps counted on the splash ("3 of 4").</summary>
    public const int TotalSteps = 4;

    /// <summary>The line under the progress bar.</summary>
    public const string StepsLine = "Settings · History · Programs · Window";

    /// <summary>
    /// Only a start the user can see gets a splash: not the tray / sign-in start, not a scheduled scan, not the
    /// "close Evict" request from Setup and not the self-cleanup run.
    /// </summary>
    public static bool ShouldShow(CommandLineOptions options) =>
        options.SelfCleanup is null && !options.Exit && !options.Headless;

    /// <summary>Text, steps done, and whether the bar is shown for a step.</summary>
    public static (string Text, int Done, bool ShowProgress) Describe(StartupStep step) => step switch
    {
        StartupStep.Starting => ("Starting…", 0, true),
        StartupStep.WaitingForAdministrator => ("Waiting for administrator permission…", 0, false),
        StartupStep.Settings => ("Reading your settings…", 0, true),
        StartupStep.History => ("Reading your history…", 1, true),
        StartupStep.Programs => ("Loading your programs…", 2, true),
        StartupStep.Window => ("Opening the window…", 3, true),
        _ => ("Starting…", 0, true),
    };

    /// <summary>"2 of 4".</summary>
    public static string CountText(StartupStep step) => $"{Math.Min(TotalSteps, Describe(step).Done + 1)} of {TotalSteps}";

    /// <summary>Bar fill 0–1: the steps done plus half of the one in progress.</summary>
    public static double Progress(StartupStep step) => Math.Clamp((Describe(step).Done + 0.5) / TotalSteps, 0, 1);

    /// <summary>A plain sentence for the error state (first line of the message, no stack trace).</summary>
    public static string ErrorText(Exception error)
    {
        var message = (error.Message ?? "").Split('\n', 2)[0].Trim();
        return message.Length == 0 ? "Something went wrong while starting." : message;
    }
}
