using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Design RD1: Ctrl+K search ranking and the single History timeline.</summary>
public sealed class RedesignRd1Tests
{
    private static readonly DateTime Now = new(2026, 10, 26, 19, 0, 0, DateTimeKind.Local);

    // ───────────── Ctrl+K search ─────────────

    [Fact]
    public void EmptyQueryMatchesEverythingInOrder()
    {
        var items = new[] { "Zoom", "Adobe Reader", "7-Zip" };
        Assert.Equal(items, QuickSearch.Top(items, "  ", s => s, null, 10));
    }

    [Fact]
    public void TitleMatchesBeatKeywordMatches()
    {
        var items = new (string Title, string Keywords)[]
        {
            ("Leftover Cleaner", "uninstall leftovers"),
            ("Uninstalling", "restore point leftovers"),
            ("Updates", "github beta"),
        };
        var top = QuickSearch.Top(items, "unin", i => i.Title, i => i.Keywords, 10);
        Assert.Equal("Uninstalling", top[0].Title);      // title starts with the query
        Assert.Equal("Leftover Cleaner", top[1].Title);  // keyword only
        Assert.Equal(2, top.Count);
    }

    [Theory]
    [InlineData("zip", "7-Zip", 200)]           // start of a word
    [InlineData("7-zip", "7-Zip", 400)]         // exact, any case
    [InlineData("adobe", "Adobe Reader", 300)]  // prefix
    [InlineData("obe", "Adobe Reader", 150)]    // inside a word
    [InlineData("reader adobe", "Adobe Reader", 120)] // every word in the title, any order
    [InlineData("chrome", "Adobe Reader", 0)]
    public void ScoresFollowTheRanking(string query, string title, int expected) =>
        Assert.Equal(expected, QuickSearch.Score(query, title));

    [Fact]
    public void EveryWordMustMatchSomewhere()
    {
        Assert.Equal(0, QuickSearch.Score("dark mode", "Appearance", "theme dark light"));
        Assert.True(QuickSearch.Score("dark theme", "Appearance", "theme dark light") > 0);
    }

    [Fact]
    public void TopRespectsTheLimit()
    {
        var items = Enumerable.Range(1, 20).Select(i => "App " + i).ToList();
        Assert.Equal(6, QuickSearch.Top(items, "app", s => s, null, 6).Count);
        Assert.Empty(QuickSearch.Top(items, "app", s => s, null, 0));
    }

    // ───────────── History timeline ─────────────

    private static UninstallHistoryEntry Uninstall(string name, DateTime when, bool ok = true) => new()
    {
        ProgramName = name, Timestamp = when, Succeeded = ok, Method = UninstallMethod.Quiet,
        LeftoversFound = 24, LeftoversRemoved = 22, BytesRemoved = 1_288_490_189,
    };

    private static SoftwareUpdateHistoryEntry Update(string name, DateTime whenLocal, SoftwareUpdateOutcome outcome = SoftwareUpdateOutcome.Updated) => new()
    {
        Name = name, PackageId = name, FromVersion = "1.2", ToVersion = "1.3", Outcome = outcome,
        StartedUtc = whenLocal.ToUniversalTime(), FinishedUtc = whenLocal.AddSeconds(42).ToUniversalTime(),
    };

    [Fact]
    public void TimelineMergesUninstallsAndUpdatesNewestFirst()
    {
        var items = ActivityTimeline.Build(
            [Uninstall("Old Tool", Now.AddDays(-2)), Uninstall("Toolbar", Now.AddHours(-1))],
            [Update("Git", Now.AddHours(-3)), Update("Running", Now, SoftwareUpdateOutcome.Running)]);

        Assert.Equal(["Uninstalled Toolbar", "Updated Git", "Uninstalled Old Tool"], items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public void TimelineFiltersByKindAndText()
    {
        UninstallHistoryEntry[] u = [Uninstall("Toolbar", Now.AddHours(-1))];
        SoftwareUpdateHistoryEntry[] up = [Update("Git", Now.AddHours(-3))];
        Assert.All(ActivityTimeline.Build(u, up, ActivityKind.Update), i => Assert.Equal(ActivityKind.Update, i.Kind));
        Assert.Single(ActivityTimeline.Build(u, up, ActivityKind.Uninstall));
        Assert.Equal("Updated Git", Assert.Single(ActivityTimeline.Build(u, up, query: "git")).Title);
        Assert.Single(ActivityTimeline.Build(u, up, query: "1.3"));   // details are searched too
    }

    [Fact]
    public void DetailsArePlainWords()
    {
        var u = ActivityTimeline.FromUninstall(Uninstall("Toolbar", Now));
        // Everything went to the Recycle Bin: removed from its place, but no disk space freed yet.
        Assert.Equal("Silent · " + SizeFormatter.Format(1_288_490_189) + " removed · 22 of 24 leftovers removed", u.Detail);

        var failed = ActivityTimeline.FromUninstall(new UninstallHistoryEntry { ProgramName = "X", Timestamp = Now, Succeeded = false, ExitCode = 1603 });
        Assert.Equal("Uninstall needs a look: X", failed.Title);
        Assert.Equal("Standard · exit code 1603", failed.Detail);

        var up = ActivityTimeline.FromUpdate(Update("Git", Now));
        Assert.Equal("1.2 → 1.3 · Updated · 42 s", up.Detail);
        Assert.Equal("Update failed: Git", ActivityTimeline.FromUpdate(Update("Git", Now, SoftwareUpdateOutcome.Failed)).Title);
        Assert.Equal("Update stopped: Git", ActivityTimeline.FromUpdate(Update("Git", Now, SoftwareUpdateOutcome.Cancelled)).Title);
    }

    [Fact]
    public void FreedSpaceIsOnlyWhatWasPermanentlyDeleted()
    {
        var partly = ActivityTimeline.UninstallDetail(new UninstallHistoryEntry { ProgramName = "X", Succeeded = true, BytesRemoved = 10_000_000, BytesReclaimed = 4_000_000 });
        Assert.Equal($"Standard · {SizeFormatter.Format(10_000_000)} removed ({SizeFormatter.Format(4_000_000)} freed)", partly);

        var all = ActivityTimeline.UninstallDetail(new UninstallHistoryEntry { ProgramName = "X", Succeeded = true, BytesRemoved = 4_000_000, BytesReclaimed = 4_000_000 });
        Assert.Equal($"Standard · {SizeFormatter.Format(4_000_000)} removed", all);

        // Older records only know the reclaimed figure.
        var legacy = ActivityTimeline.UninstallDetail(new UninstallHistoryEntry { ProgramName = "X", Succeeded = true, BytesReclaimed = 4_000_000 });
        Assert.Equal($"Standard · {SizeFormatter.Format(4_000_000)} freed", legacy);
    }

    [Fact]
    public void ByDayGroupsWithTodayAndYesterdayLabels()
    {
        var items = ActivityTimeline.Build(
            [Uninstall("A", Now.AddHours(-1)), Uninstall("B", Now.AddDays(-1)), Uninstall("C", Now.AddDays(-3))], []);
        var days = ActivityTimeline.ByDay(items, Now);
        Assert.Equal(["Today · Mon 26 Oct", "Yesterday · Sun 25 Oct", "Fri 23 Oct"], days.Select(d => d.Label).ToArray());
    }
}
