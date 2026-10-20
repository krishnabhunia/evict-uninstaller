using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Design SH1: the Software Updater keeps a 7-day update history (H1 · E1 · D1).</summary>
public sealed class SoftwareUpdateHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evict-sh1-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(_dir, "update-history.json");
    private DateTime _nowLocal = new(2026, 10, 19, 17, 0, 0, DateTimeKind.Local);

    public SoftwareUpdateHistoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SoftwareUpdateHistoryStore NewStore() => new(File_, () => _nowLocal.ToUniversalTime());

    private static SoftwareUpdateHistoryEntry Entry(DateTime startedLocal, SoftwareUpdateOutcome outcome = SoftwareUpdateOutcome.Updated, string name = "Git") => new()
    {
        Name = name, PackageId = name + ".Id", FromVersion = "1.0", ToVersion = "1.1",
        StartedUtc = startedLocal.ToUniversalTime(), FinishedUtc = startedLocal.AddSeconds(44).ToUniversalTime(), Outcome = outcome,
    };

    [Fact]
    public void KeepsTodayAndTheSixDaysBefore()
    {
        Assert.Equal(new DateTime(2026, 10, 13), SoftwareUpdateHistory.OldestKeptDay(_nowLocal));
        Assert.True(SoftwareUpdateHistory.IsKept(Entry(new DateTime(2026, 10, 13, 0, 0, 1, DateTimeKind.Local)), _nowLocal));
        Assert.False(SoftwareUpdateHistory.IsKept(Entry(new DateTime(2026, 10, 12, 23, 59, 59, DateTimeKind.Local)), _nowLocal));
    }

    [Fact]
    public void AddDropsEntriesOlderThanSevenDaysAndKeepsNewestFirst()
    {
        var store = NewStore();
        store.Add(Entry(new DateTime(2026, 10, 12, 19, 10, 0, DateTimeKind.Local), name: "Old"));
        store.Add(Entry(new DateTime(2026, 10, 13, 19, 10, 0, DateTimeKind.Local), name: "Edge"));
        store.Add(Entry(new DateTime(2026, 10, 19, 16, 42, 0, DateTimeKind.Local), name: "New"));

        Assert.Equal(new[] { "New", "Edge" }, store.Entries.Select(e => e.Name));
    }

    [Fact]
    public void LoadPrunesOnANewDayAndKeepsOnlyFinishedResults()
    {
        var store = NewStore();
        store.Add(Entry(new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Local), name: "Tuesday"));
        store.Add(Entry(new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Local), name: "Today"));
        store.Add(Entry(new DateTime(2026, 10, 19, 9, 5, 0, DateTimeKind.Local), SoftwareUpdateOutcome.Running, "Unfinished"));

        _nowLocal = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Local);
        var reloaded = NewStore();
        reloaded.Load();

        Assert.Equal(new[] { "Today" }, reloaded.Entries.Select(e => e.Name));
    }

    [Fact]
    public void PersistsAndClears()
    {
        var store = NewStore();
        var entry = Entry(new DateTime(2026, 10, 18, 21, 3, 0, DateTimeKind.Local), SoftwareUpdateOutcome.Failed, "Zoom");
        entry.Message = "Zoom was open";
        entry.Output = Enumerable.Range(1, 60).Select(i => "line " + i).ToList();
        store.Add(entry);

        var reloaded = NewStore();
        reloaded.Load();
        var saved = Assert.Single(reloaded.Entries);
        Assert.Equal(SoftwareUpdateOutcome.Failed, saved.Outcome);
        Assert.Equal("Zoom was open", saved.Message);
        Assert.Equal(SoftwareUpdateHistory.MaxOutputLines, saved.Output.Count);
        Assert.Equal("line 60", saved.Output[^1]);

        reloaded.Clear();
        var empty = NewStore();
        empty.Load();
        Assert.Empty(empty.Entries);
    }

    [Fact]
    public void ACorruptFileStartsEmptyInsteadOfThrowing()
    {
        File.WriteAllText(File_, "{ not json");
        var store = NewStore();
        store.Load();
        Assert.Empty(store.Entries);
    }

    [Fact]
    public void SelfUpdateThatReachedTheNewVersionIsRecordedAsUpdated()
    {
        var store = NewStore();
        store.BeginSelfUpdate("1.14.0", "2.0.0", "win-v2.0.0", "Auto update", new[] { "SHA-256 matches the release" });
        _nowLocal = _nowLocal.AddSeconds(24);

        var reloaded = NewStore();
        reloaded.Load();
        var entry = reloaded.ResolveSelfUpdate("2.0.0+abc123");

        Assert.NotNull(entry);
        Assert.True(entry!.IsEvict);
        Assert.Equal(SoftwareUpdateOutcome.Updated, entry.Outcome);
        Assert.Equal("1.14.0", entry.FromVersion);
        Assert.Equal("2.0.0", entry.ToVersion);
        Assert.Equal("GitHub release win-v2.0.0", entry.Source);
        Assert.Equal(TimeSpan.FromSeconds(24), entry.Duration);
        Assert.Null(reloaded.Pending);
        Assert.Single(reloaded.Entries);
        Assert.Null(reloaded.ResolveSelfUpdate("2.0.0")); // resolved once only
    }

    [Fact]
    public void SelfUpdateThatDidNotFinishIsRecordedAsFailed()
    {
        var store = NewStore();
        store.BeginSelfUpdate("1.14.0", "2.0.0", "win-v2.0.0", "You");
        var entry = store.ResolveSelfUpdate("1.14.0");

        Assert.Equal(SoftwareUpdateOutcome.Failed, entry!.Outcome);
        Assert.Contains("still on 1.14.0", entry.Message);
    }

    [Fact]
    public void SelfUpdateFailureBeforeRestartIsRecordedAtOnce()
    {
        var store = NewStore();
        store.BeginSelfUpdate("1.14.0", "2.0.0", null, "You");
        store.FailSelfUpdate("1.14.0", "2.0.0", null, "You", _nowLocal.ToUniversalTime(), "No permission to replace Evict.exe.");

        var entry = Assert.Single(store.Entries);
        Assert.Equal(SoftwareUpdateOutcome.Failed, entry.Outcome);
        Assert.Equal("No permission to replace Evict.exe. Evict is still on 1.14.0.", entry.Message);
        Assert.Null(store.Pending);
        Assert.Null(store.ResolveSelfUpdate("1.14.0"));
    }

    [Fact]
    public void VersionChangedOutsideEvictIsRecordedButFirstRunAndSameVersionAreNot()
    {
        var store = NewStore();
        Assert.Null(store.ResolveSelfUpdate("1.14.0"));   // first run: nothing to compare with
        Assert.Null(store.ResolveSelfUpdate("1.14.0"));   // same version
        var entry = store.ResolveSelfUpdate("2.0.0");

        Assert.Equal(SoftwareUpdateOutcome.Updated, entry!.Outcome);
        Assert.Equal("Installed outside Evict", entry.Source);
        Assert.Equal("1.14.0", entry.FromVersion);
        Assert.Null(store.ResolveSelfUpdate("1.9.0"));    // going back a version is not an update
    }

    [Theory]
    [InlineData(19, "Today · Mon 19 Oct")]
    [InlineData(18, "Yesterday · Sun 18 Oct")]
    [InlineData(16, "Fri 16 Oct")]
    public void DayLabels(int day, string expected) =>
        Assert.Equal(expected, SoftwareUpdateHistory.DayLabel(new DateTime(2026, 10, day, 9, 0, 0), new DateTime(2026, 10, 19, 17, 0, 0)));

    [Theory]
    [InlineData(6, "6 s")]
    [InlineData(72, "1 min 12 s")]
    [InlineData(120, "2 min")]
    [InlineData(3780, "1 h 3 min")]
    public void Durations(int seconds, string expected) =>
        Assert.Equal(expected, SoftwareUpdateHistory.DurationText(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void UpdatesTabNotesOnlyFailedOrCancelledLastTries()
    {
        var now = new DateTime(2026, 10, 19, 17, 0, 0, DateTimeKind.Local);
        var failed = Entry(new DateTime(2026, 10, 19, 16, 41, 0, DateTimeKind.Local), SoftwareUpdateOutcome.Failed, "Zoom");
        var older = Entry(new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Local), SoftwareUpdateOutcome.Updated, "Zoom");
        var cancelled = Entry(new DateTime(2026, 10, 18, 21, 2, 0, DateTimeKind.Local), SoftwareUpdateOutcome.Cancelled, "Python");
        var evict = Entry(now.AddHours(-1), SoftwareUpdateOutcome.Failed, "Zoom");
        evict.IsEvict = true;
        var all = new[] { older, failed, cancelled, evict };

        Assert.Same(failed, SoftwareUpdateHistory.LatestFor(all, "zoom.id"));
        Assert.Equal("Failed today 16:41 — why?", SoftwareUpdateHistory.LastResultNote(failed, now));
        Assert.Equal("Cancelled yesterday 21:02", SoftwareUpdateHistory.LastResultNote(cancelled, now));
        Assert.Equal("", SoftwareUpdateHistory.LastResultNote(older, now));
        Assert.Equal("", SoftwareUpdateHistory.LastResultNote(null, now));
        Assert.Equal("Fri 16 Oct", SoftwareUpdateHistory.WhenText(new DateTime(2026, 10, 16, 11, 30, 0, DateTimeKind.Local).ToUniversalTime(), now));
    }

    [Fact]
    public void UnknownDurationIsADash() => Assert.Equal("—", SoftwareUpdateHistory.DurationText(null));

    [Fact]
    public void DescribesEachOutcomeInPlainWords()
    {
        Assert.Equal(SoftwareUpdateOutcome.Cancelled, SoftwareUpdateHistory.Describe("Python", "3.12.10", false, "Cancelled.", cancelled: true).Outcome);
        Assert.Equal(SoftwareUpdateOutcome.UpdatedRestartNeeded, SoftwareUpdateHistory.Describe("PowerToys", "0.94.2", true, WingetService.RestartToFinishMessage, false).Outcome);
        Assert.Equal((SoftwareUpdateOutcome.Updated, "Git is on the new version."), SoftwareUpdateHistory.Describe("Git", "2.51.0", true, "Updated.", false));

        var failed = SoftwareUpdateHistory.Describe("Zoom Workplace", "6.5.3", false, "the application is in use – close it and retry (0x8A150101).", false);
        Assert.Equal(SoftwareUpdateOutcome.Failed, failed.Outcome);
        Assert.Equal("The application is in use – close it and retry (0x8A150101). Nothing changed — Zoom Workplace is still on 6.5.3.", failed.Message);
    }

    [Fact]
    public void SummaryCountsFinishedResults()
    {
        var day = new DateTime(2026, 10, 19, 9, 0, 0, DateTimeKind.Local);
        var entries = new[]
        {
            Entry(day), Entry(day, SoftwareUpdateOutcome.UpdatedRestartNeeded), Entry(day, SoftwareUpdateOutcome.Failed),
            Entry(day, SoftwareUpdateOutcome.Cancelled), Entry(day, SoftwareUpdateOutcome.Running),
        };
        Assert.Equal("2 updated · 1 failed · 1 cancelled", SoftwareUpdateHistory.Summary(entries));
        Assert.Null(SoftwareUpdateHistory.Summary(Array.Empty<SoftwareUpdateHistoryEntry>()));
    }

    [Fact]
    public void CsvHasAHeaderAndEscapesQuotes()
    {
        var e = Entry(new DateTime(2026, 10, 19, 16, 42, 0, DateTimeKind.Local), name: "Say \"hi\"");
        var csv = SoftwareUpdateHistory.ToCsv(new[] { e }).Split(Environment.NewLine);
        Assert.StartsWith("Started,Finished,Program,Package,From,To,Result,Took", csv[0]);
        Assert.Contains("\"Say \"\"hi\"\"\"", csv[1]);
        Assert.Contains("\"2026-10-19 16:42:00\"", csv[1]);
        Assert.Contains("\"44 s\"", csv[1]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-cmd", "\"'-cmd\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("Git", "\"Git\"")]
    [InlineData(null, "\"\"")]
    public void CsvCellsNeverStartAFormula(string? value, string expected) =>
        Assert.Equal(expected, SoftwareUpdateHistory.Csv(value));

    [Fact]
    public void CancelledSelfUpdateIsRecorded()
    {
        var store = NewStore();
        var started = _nowLocal.AddSeconds(-14).ToUniversalTime();
        store.CancelSelfUpdate("2.0.0", "2.1.0", "win-v2.1.0", "You", started, new[] { "Found Evict 2.1.0 on GitHub" });

        var entry = Assert.Single(store.Entries);
        Assert.Equal(SoftwareUpdateOutcome.Cancelled, entry.Outcome);
        Assert.True(entry.IsEvict);
        Assert.Equal(TimeSpan.FromSeconds(14), entry.Duration);
        Assert.Equal(new[] { "Found Evict 2.1.0 on GitHub", "Cancelled" }, entry.Output);
        Assert.Null(store.Pending);
    }

    [Fact]
    public void LongOutputLinesAreShortened()
    {
        var trimmed = SoftwareUpdateHistory.TrimOutput(new[] { "", "  ", new string('x', 500) });
        var line = Assert.Single(trimmed);
        Assert.Equal(SoftwareUpdateHistory.MaxOutputLineLength + 1, line.Length);
    }

    private static readonly UpgradablePackage Pkg = new() { Name = "PowerToys", Id = "Microsoft.PowerToys", InstalledVersion = "0.94.2", AvailableVersion = "0.95.0", Source = "winget" };

    [Theory]
    [InlineData(unchecked((int)0x8A150109), "")]
    [InlineData(3010, "")]
    [InlineData(1, "Restart your PC to finish installation.")]
    public async Task RestartToFinishIsASuccessThatSaysSo(int exitCode, string output)
    {
        var service = new WingetService(() => "fake-winget.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = exitCode, StdOut = output }));

        var (ok, message) = await service.UpgradeAsync(Pkg, CancellationToken.None);

        Assert.True(ok);
        Assert.True(WingetService.IsRestartToFinish(message));
    }

    [Fact]
    public async Task PlainSuccessIsNotARestart()
    {
        var service = new WingetService(() => "fake-winget.exe", (_, _, _, _, _) =>
            Task.FromResult(new ProcessResult { ExitCode = 0, StdOut = "Successfully installed" }));
        var (ok, message) = await service.UpgradeAsync(Pkg, CancellationToken.None);
        Assert.True(ok);
        Assert.False(WingetService.IsRestartToFinish(message));
    }
}
