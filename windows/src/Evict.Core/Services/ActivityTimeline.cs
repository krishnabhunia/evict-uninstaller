using System.Globalization;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

public enum ActivityKind { Uninstall, Update }

/// <summary>One line of the RD1 History timeline (and the Home "Recently" list).</summary>
public sealed record ActivityItem(ActivityKind Kind, Guid Id, DateTime WhenLocal, string Title, string Detail, bool Succeeded);

/// <summary>
/// RD1: one History for everything Evict did – uninstalls (kept until cleared) and program updates (kept 7 days) –
/// newest first and grouped by day.
/// </summary>
public static class ActivityTimeline
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static IReadOnlyList<ActivityItem> Build(IEnumerable<UninstallHistoryEntry> uninstalls, IEnumerable<SoftwareUpdateHistoryEntry> updates,
        ActivityKind? only = null, string? query = null)
    {
        var items = new List<ActivityItem>();
        if (only is null or ActivityKind.Uninstall)
            items.AddRange(uninstalls.Select(FromUninstall));
        if (only is null or ActivityKind.Update)
            items.AddRange(updates.Where(u => u.IsFinished).Select(FromUpdate));

        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q))
            items = items.Where(i => i.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        return items.OrderByDescending(i => i.WhenLocal).ToList();
    }

    public static ActivityItem FromUninstall(UninstallHistoryEntry e) => new(
        ActivityKind.Uninstall, e.Id, e.Timestamp,
        e.Succeeded ? "Uninstalled " + e.ProgramName : "Uninstall needs a look: " + e.ProgramName,
        UninstallDetail(e), e.Succeeded);

    public static ActivityItem FromUpdate(SoftwareUpdateHistoryEntry e) => new(
        ActivityKind.Update, e.Id, e.StartedUtc.ToLocalTime(),
        e.Outcome switch
        {
            SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.UpdatedRestartNeeded => "Updated " + e.Name,
            SoftwareUpdateOutcome.Cancelled => "Update stopped: " + e.Name,
            _ => "Update failed: " + e.Name,
        },
        UpdateDetail(e), e.IsSuccess);

    /// <summary>"Silent · 1.2 GB freed · 22 of 24 leftovers removed".</summary>
    public static string UninstallDetail(UninstallHistoryEntry e)
    {
        var parts = new List<string>
        {
            e.Method switch
            {
                UninstallMethod.Quiet => "Silent",
                UninstallMethod.Force => "Force uninstall",
                UninstallMethod.InstallLog => "From install log",
                UninstallMethod.RegistryEntryOnly => "Entry removed",
                _ => "Standard",
            },
        };
        var bytes = e.BytesRemoved > 0 ? e.BytesRemoved : e.BytesReclaimed;
        if (bytes > 0) parts.Add(SizeFormatter.Format(bytes) + " freed");
        if (e.LeftoversFound > 0)
            parts.Add(e.LeftoversRemoved == e.LeftoversFound ? $"{e.LeftoversFound} leftovers removed" : $"{e.LeftoversRemoved} of {e.LeftoversFound} leftovers removed");
        if (!e.Succeeded && e.ExitCode is { } code) parts.Add("exit code " + code.ToString(En));
        return string.Join(" · ", parts);
    }

    /// <summary>"1.2.0 → 1.3.0 · Updated · 42 s".</summary>
    public static string UpdateDetail(SoftwareUpdateHistoryEntry e)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(e.FromVersion) || !string.IsNullOrEmpty(e.ToVersion))
            parts.Add($"{(string.IsNullOrEmpty(e.FromVersion) ? "?" : e.FromVersion)} → {(string.IsNullOrEmpty(e.ToVersion) ? "latest" : e.ToVersion)}");
        parts.Add(SoftwareUpdateHistory.OutcomeText(e.Outcome));
        if (e.Duration is not null) parts.Add(SoftwareUpdateHistory.DurationText(e.Duration));
        return string.Join(" · ", parts);
    }

    /// <summary>The items split into days (newest day first), each with its "Today · Mon 26 Oct" label.</summary>
    public static IReadOnlyList<(string Label, IReadOnlyList<ActivityItem> Items)> ByDay(IReadOnlyList<ActivityItem> items, DateTime nowLocal) =>
        items.GroupBy(i => i.WhenLocal.Date)
             .OrderByDescending(g => g.Key)
             .Select(g => (SoftwareUpdateHistory.DayLabel(g.Key, nowLocal), (IReadOnlyList<ActivityItem>)g.ToList()))
             .ToList();
}
