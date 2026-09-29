namespace Evict.Core.Util;

/// <summary>
/// Parsed command line. Supported:
///   --uninstall-file "C:\path\app.exe|shortcut.lnk"   uninstall the program that owns this file
///   --uninstall "Program Name"                          uninstall by (partial) display name
///   --scan                                              open Software Health and run a scan
///   --widget                                            show the Easy Uninstall widget
///   --page programs|apps|extensions|updater|monitor|tools|history|settings|health
///   --updated                                           (internal) first start after a self-update – show the "updated" notice
///   --tray                                              start hidden in the notification area (used by "Start with Windows")
///   --scheduled-scan                                    run the Software Health scan in the background and notify (Task Scheduler)
///   --self-cleanup ask|&lt;parts&gt;                           remove Evict's own leftovers: "ask" shows the checklist (uninstaller),
///                                                       parts ("integration", "settings,history", "all") run silently
///   --wait-pid N                                        (with --self-cleanup) wait for process N to exit first
///   --portable                                          (with --self-cleanup) started from a portable copy's Settings
///   --exit                                              ask a running Evict to close (Setup, when Evict runs as administrator)
///   --no-elevate                                        do not ask for administrator rights at this start ("Start as administrator")
///   --wait-pid N                                        (without --self-cleanup) wait for process N to exit before starting –
///                                                       used by the elevated copy that replaces a non-elevated one
/// Pure logic – unit tested.
/// </summary>
public sealed class CommandLineOptions
{
    public string? UninstallFile { get; init; }
    public string? UninstallName { get; init; }
    public bool Scan { get; init; }
    public bool Widget { get; init; }
    public string? Page { get; init; }
    public bool Updated { get; init; }
    public bool Tray { get; init; }
    public bool ScheduledScan { get; init; }
    /// <summary>"ask" or a parts list for <c>SelfCleanupService.ParseParts</c>; null when not requested.</summary>
    public string? SelfCleanup { get; init; }
    public int? WaitPid { get; init; }
    public bool Portable { get; init; }
    public bool Exit { get; init; }
    public bool NoElevate { get; init; }
    public bool SelfCleanupAsk => string.Equals(SelfCleanup, "ask", StringComparison.OrdinalIgnoreCase);
    public List<string> Unknown { get; } = new();

    public bool IsEmpty => UninstallFile is null && UninstallName is null && !Scan && !Widget && Page is null && !Updated && !Tray && !ScheduledScan && SelfCleanup is null && !Exit;
    /// <summary>True when the process should start without showing the main window.</summary>
    public bool Headless => Tray || ScheduledScan;

    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? file = null, name = null, page = null, selfCleanup = null;
        int? waitPid = null;
        bool scan = false, widget = false, updated = false, tray = false, scheduled = false, portable = false, exit = false, noElevate = false;
        var unknown = new List<string>();

        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i].Trim();
            if (a.Length == 0) continue;
            var key = a.TrimStart('-', '/').ToLowerInvariant();
            string? Next() => i + 1 < args.Count ? args[++i].Trim().Trim('"') : null;

            switch (key)
            {
                case "uninstall-file" or "uninstallfile" or "file": file = Next(); break;
                case "uninstall" or "name": name = Next(); break;
                case "scan" or "health": scan = true; break;
                case "widget" or "easy": widget = true; break;
                case "page": page = Next()?.ToLowerInvariant(); break;
                case "updated": updated = true; break;
                case "tray" or "background" or "minimized": tray = true; break;
                case "scheduled-scan" or "scheduledscan" or "background-scan": scheduled = true; break;
                case "self-cleanup" or "selfcleanup":
                    // The value is optional: "--self-cleanup --wait-pid 1" means "ask".
                    selfCleanup = i + 1 < args.Count && !args[i + 1].TrimStart().StartsWith('-') ? Next() : "ask";
                    if (string.IsNullOrWhiteSpace(selfCleanup)) selfCleanup = "ask";
                    break;
                case "wait-pid" or "waitpid":
                    if (int.TryParse(Next(), out var pid) && pid > 0) waitPid = pid;
                    break;
                case "portable": portable = true; break;
                case "exit" or "quit": exit = true; break;
                case "no-elevate" or "noelevate": noElevate = true; break;
                default:
                    // A bare path (drag & drop onto the exe, or "Open with") means --uninstall-file.
                    if (!a.StartsWith('-') && !a.StartsWith('/') && (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
                        file = a.Trim('"');
                    else unknown.Add(a);
                    break;
            }
        }
        var opts = new CommandLineOptions { UninstallFile = file, UninstallName = name, Scan = scan, Widget = widget, Page = page, Updated = updated, Tray = tray, ScheduledScan = scheduled, SelfCleanup = selfCleanup, WaitPid = waitPid, Portable = portable, Exit = exit, NoElevate = noElevate };
        opts.Unknown.AddRange(unknown);
        return opts;
    }

    /// <summary>
    /// Builds a command line that CommandLineToArgvW splits back into exactly these arguments: arguments with spaces,
    /// tabs or quotes are quoted, embedded quotes escaped and backslashes doubled where they precede a quote.
    /// </summary>
    public static string JoinArguments(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteArgument));

    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>Serialises for the single-instance pipe: one argument per line.</summary>
    public static string Pack(IReadOnlyList<string> args) => string.Join("\n", args.Select(a => a.Replace("\n", " ")));
    public static string[] Unpack(string payload) => payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
