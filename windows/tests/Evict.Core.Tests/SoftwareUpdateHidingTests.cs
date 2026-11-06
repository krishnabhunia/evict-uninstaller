using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Design VM1: generic rules that keep never-ending "updates" out of the list for every package.</summary>
public sealed class SoftwareUpdateHidingTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 14, 30, 0, DateTimeKind.Utc);
    private static readonly SoftwareUpdateHidingOptions OptionB = new();

    private static UpgradablePackage Pkg(string installed, string available, string id = "Tonec.InternetDownloadManager",
        string? source = "winget", string name = "Internet Download Manager") =>
        new() { Id = id, Name = name, InstalledVersion = installed, AvailableVersion = available, Source = source };

    private static SoftwareUpdateView Classify(IEnumerable<UpgradablePackage> packages, IEnumerable<HiddenUpdateRecord>? records = null,
        IEnumerable<SoftwareUpdateExclusion>? exclusions = null, SoftwareUpdateHidingOptions? options = null) =>
        SoftwareUpdateFilter.Classify(packages, exclusions, records, options ?? OptionB);

    // ── rule 3: same build, repackaged ──

    [Theory]
    [InlineData("6.43.18", "6.43.18.1")]
    [InlineData("1.2.3", "1.2.3.7")]
    [InlineData("v24.8.0", "24.8.0.2")]
    [InlineData("10.0.19041", "10.0.19041.3")]
    public void RepackagedBuildIsRecognisedForAnyPackage(string installed, string available) =>
        Assert.True(SoftwareVersionMatch.IsRepackage(installed, available));

    [Theory]
    [InlineData("6.43", "6.43.1")]          // two-part version + patch = real release
    [InlineData("1.2.3.0", "1.2.3.1")]      // same length = real update
    [InlineData("6.43.18", "6.43.19")]
    [InlineData("6.43.18", "6.43.18.0")]    // that is rule 2, not a repackage
    [InlineData("6.43.18", "6.43.18.1.2")]  // more than one extra number
    [InlineData("6.43.17", "6.43.18.1")]
    [InlineData("Unknown", "6.43.18.1")]
    [InlineData("< 6.43.18", "6.43.18.1")]
    [InlineData("1.2.3-beta", "1.2.3.1")]
    [InlineData(null, "6.43.18.1")]
    public void RealUpdatesAreNotTreatedAsRepackages(string? installed, string available) =>
        Assert.False(SoftwareVersionMatch.IsRepackage(installed, available));

    [Fact]
    public void IdmOfferIsHiddenByDefault()
    {
        var view = Classify([Pkg("6.43.18", "6.43.18.1"), Pkg("8.6.9", "8.7.1", "Notepad++.Notepad++", name: "Notepad++")]);
        Assert.Equal("Notepad++.Notepad++", Assert.Single(view.Visible).Id);
        var hidden = Assert.Single(view.Hidden);
        Assert.Equal(HiddenUpdateReason.Repackaged, hidden.Reason);
        Assert.True(hidden.IsAutomatic);
    }

    [Fact]
    public void RepackageRuleCanBeTurnedOff()
    {
        var view = Classify([Pkg("6.43.18", "6.43.18.1")], options: new(HideUnchanged: true, HideRepackaged: false));
        Assert.Single(view.Visible);
        Assert.Empty(view.Hidden);
    }

    // ── rule 2: same version, different spelling ──

    [Theory]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v5.1", "5.1")]
    [InlineData(" 3.0.0 ", "3")]
    [InlineData("2.4.0.0", "2.4")]
    public void SameVersionWrittenDifferentlyIsHidden(string installed, string available)
    {
        Assert.True(SoftwareVersionMatch.IsSameVersion(installed, available));
        Assert.Equal(HiddenUpdateReason.SameVersion, Assert.Single(Classify([Pkg(installed, available, "Any.App")]).Hidden).Reason);
    }

    [Theory]
    [InlineData("1.2", "1.2.1")]
    [InlineData("1.10", "1.1")]
    [InlineData("Unknown", "1.0")]
    [InlineData("", "1.0")]
    public void DifferentVersionsAreNotTheSame(string installed, string available) =>
        Assert.False(SoftwareVersionMatch.IsSameVersion(installed, available));

    [Fact]
    public void UnchangedRulesCanBeTurnedOff()
    {
        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate([], [Pkg("2.0", "2.1", "Stuck.App")], Now);
        var view = Classify([Pkg("1.2", "1.2.0.0", "Any.App"), Pkg("2.0", "2.1", "Stuck.App")], records,
            options: new(HideUnchanged: false, HideRepackaged: false));
        Assert.Equal(2, view.Visible.Count);
    }

    // ── rule 1: unchanged after an update ──

    [Fact]
    public void UpdateThatChangedNothingIsDetectedAndHidden()
    {
        var before = Pkg("4.0", "4.1", "Stuck.App");
        var unchanged = SoftwareUpdateFilter.FindUnchanged([before, Pkg("1.0", "2.0", "Fixed.App")],
            [Pkg("4.0", "4.1", "stuck.app", source: " WINGET ")]);
        Assert.Equal("Stuck.App", Assert.Single(unchanged).Id);

        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate([], unchanged, Now);
        var hidden = Assert.Single(Classify([before], records).Hidden);
        Assert.Equal(HiddenUpdateReason.AlreadyUpToDate, hidden.Reason);
        Assert.Equal(Now, hidden.SinceUtc);
    }

    [Fact]
    public void UpdateThatMovedTheInstalledVersionIsNotStuck() =>
        Assert.Empty(SoftwareUpdateFilter.FindUnchanged([Pkg("4.0", "4.2", "A.App")], [Pkg("4.1", "4.2", "A.App")]));

    [Fact]
    public void AlreadyUpToDateComesBackWhenTheOfferChanges()
    {
        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate([], [Pkg("4.0", "4.1", "Stuck.App")], Now);
        Assert.Single(Classify([Pkg("4.0", "4.2", "Stuck.App")], records).Visible);
        Assert.Single(Classify([Pkg("4.1", "4.1.5", "Stuck.App")], records).Visible);
    }

    [Fact]
    public void MarkingTwiceKeepsOneRecordAndReplacesShowAnyway()
    {
        var p = Pkg("4.0", "4.1", "Stuck.App");
        var restored = SoftwareUpdateFilter.Restore([], [new HiddenUpdate(p, HiddenUpdateReason.AlreadyUpToDate, Now)], Now);
        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate(SoftwareUpdateFilter.MarkAlreadyUpToDate(restored, [p], Now), [p], Now);
        Assert.Equal(HiddenUpdateRecordKind.AlreadyUpToDate, Assert.Single(records).Kind);
    }

    // ── rule 4: skip this version ──

    [Fact]
    public void SkippedVersionHidesOnlyThatVersion()
    {
        var records = SoftwareUpdateFilter.SkipVersions([], [Pkg("8.6.9", "8.7.1", "Notepad++.Notepad++")], Now);
        Assert.Equal(HiddenUpdateReason.SkippedVersion, Assert.Single(Classify([Pkg("8.6.9", "8.7.1", "Notepad++.Notepad++")], records).Hidden).Reason);
        // Even after updating something else: the skip follows the available version, not the installed one.
        Assert.Single(Classify([Pkg("8.7.0", "8.7.1", "Notepad++.Notepad++")], records).Hidden);
        Assert.Single(Classify([Pkg("8.6.9", "8.7.2", "Notepad++.Notepad++")], records).Visible);
    }

    [Fact]
    public void SkippingAgainReplacesTheEarlierSkip()
    {
        var records = SoftwareUpdateFilter.SkipVersions([], [Pkg("1.0", "1.1", "A.App")], Now);
        records = SoftwareUpdateFilter.SkipVersions(records, [Pkg("1.0", "1.2", "A.App")], Now);
        Assert.Equal("1.2", Assert.Single(records).AvailableVersion);
    }

    [Fact]
    public void SkipBeatsShowAnywayAndExclusionBeatsEverything()
    {
        var p = Pkg("6.43.18", "6.43.18.1");
        var records = SoftwareUpdateFilter.Restore([], [new HiddenUpdate(p, HiddenUpdateReason.Repackaged, null)], Now);
        records = SoftwareUpdateFilter.SkipVersions(records, [p], Now);
        Assert.Equal(HiddenUpdateReason.SkippedVersion, Assert.Single(Classify([p], records).Hidden).Reason);
        var excluded = SoftwareUpdateExclusion.Add([], [p]);
        Assert.Equal(HiddenUpdateReason.Excluded, Assert.Single(Classify([p], records, excluded).Hidden).Reason);
    }

    [Fact]
    public void SameIdFromAnotherSourceIsAnotherPackage()
    {
        var records = SoftwareUpdateFilter.SkipVersions([], [Pkg("1.0", "1.1", "A.App")], Now);
        Assert.Single(Classify([Pkg("1.0", "1.1", "A.App", source: "msstore")], records).Visible);
    }

    // ── restore ──

    [Fact]
    public void RestoringAnAutomaticHideListsThatExactOfferAgain()
    {
        var p = Pkg("6.43.18", "6.43.18.1");
        var hidden = Assert.Single(Classify([p]).Hidden);
        var records = SoftwareUpdateFilter.Restore([], [hidden], Now);
        Assert.Single(Classify([p], records).Visible);
        // A later repackage of another build is hidden again by the rule.
        Assert.Single(Classify([Pkg("6.43.19", "6.43.19.1")], records).Hidden);
    }

    [Fact]
    public void RestoringAStuckOfferRemovesItsRecord()
    {
        var p = Pkg("6.43.18", "6.43.18.1");
        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate([], [p], Now);
        records = SoftwareUpdateFilter.Restore(records, [new HiddenUpdate(p, HiddenUpdateReason.AlreadyUpToDate, Now)], Now);
        Assert.Equal(HiddenUpdateRecordKind.ShowAnyway, Assert.Single(records).Kind);
        Assert.Single(Classify([p], records).Visible);
    }

    [Fact]
    public void RestoringASkipRemovesIt()
    {
        var p = Pkg("1.0", "1.1", "A.App");
        var records = SoftwareUpdateFilter.SkipVersions([], [p], Now);
        records = SoftwareUpdateFilter.Restore(records, [new HiddenUpdate(p, HiddenUpdateReason.SkippedVersion, Now)], Now);
        Assert.Empty(records);
    }

    // ── pruning and persistence ──

    [Fact]
    public void PruneDropsRecordsWhoseOfferChangedButKeepsMissingPackages()
    {
        var records = SoftwareUpdateFilter.MarkAlreadyUpToDate([], [Pkg("4.0", "4.1", "Stuck.App"), Pkg("1.0", "1.1", "Gone.App")], Now);
        records = SoftwareUpdateFilter.SkipVersions(records, [Pkg("2.0", "2.1", "Skip.App")], Now);
        var pruned = SoftwareUpdateFilter.Prune(records, [Pkg("4.1", "4.2", "Stuck.App"), Pkg("2.0", "2.1", "Skip.App")]);
        Assert.Equal(["Gone.App", "Skip.App"], pruned.Select(r => r.Id).Order().ToArray());
    }

    [Fact]
    public void RecordsSurviveASettingsRoundTrip()
    {
        var settings = new AppSettings
        {
            HiddenSoftwareUpdates = SoftwareUpdateFilter.SkipVersions(
                SoftwareUpdateFilter.MarkAlreadyUpToDate([], [Pkg("6.43.18", "6.43.18.1")], Now), [Pkg("8.6.9", "8.7.1", "N.App")], Now),
        };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"AlreadyUpToDate\"", json);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal(2, Classify([Pkg("6.43.18", "6.43.18.1"), Pkg("8.6.9", "8.7.1", "N.App")], loaded.HiddenSoftwareUpdates,
            options: new(HideUnchanged: true, HideRepackaged: false)).Hidden.Count);
    }

    [Fact]
    public void OldSettingsFilesGetOptionBDefaults()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{\"SettingsVersion\":2,\"HiddenSoftwareUpdates\":null}")!;
        SettingsStore.Migrate(loaded);
        Assert.NotNull(loaded.HiddenSoftwareUpdates);
        Assert.True(loaded.HideUnchangedSoftwareUpdates);
        Assert.True(loaded.HideRepackagedSoftwareUpdates);
        Assert.Equal(new SoftwareUpdateHidingOptions(true, true), SoftwareUpdateHidingOptions.From(loaded));
    }

    // ── winget "nothing to install" results ──

    [Theory]
    [InlineData(0x8A150061)]
    [InlineData(0x8A15010D)]
    [InlineData(0x8A15004F)]
    public void WingetNothingNewerCodesCountAsAlreadyCurrent(uint code) =>
        Assert.True(WingetService.IsAlreadyCurrent(unchecked((int)code)));

    [Theory]
    [InlineData(0)]
    [InlineData(0x8A15002B)]
    [InlineData(0x8A150011)]
    public void OtherCodesAreNotAlreadyCurrent(uint code) =>
        Assert.False(WingetService.IsAlreadyCurrent(unchecked((int)code)));

    [Fact]
    public async Task AlreadyInstalledResultIsASuccessWithAClearMessage()
    {
        var winget = new WingetService(() => "winget.exe",
            (_, _, _, _, _) => Task.FromResult(new ProcessResult { ExitCode = unchecked((int)0x8A150061), StdOut = "Package already installed", StdErr = "" }));
        var result = await winget.UpgradeWithResultAsync(Pkg("6.43.18", "6.43.18.1"), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(WingetService.IsAlreadyCurrent(result.Message));
        var (outcome, message) = SoftwareUpdateHistory.Describe("Internet Download Manager", "6.43.18", true, result.Message, false);
        Assert.Equal(SoftwareUpdateOutcome.Updated, outcome);
        Assert.StartsWith("No change", message);
    }

    // ── banner text ──

    [Fact]
    public void SummaryNamesEachKind()
    {
        var p = Pkg("6.43.18", "6.43.18.1");
        var hidden = new List<HiddenUpdate>
        {
            new(p, HiddenUpdateReason.Repackaged, null),
            new(Pkg("1.0", "1.1", "S.App"), HiddenUpdateReason.SkippedVersion, Now),
        };
        Assert.Equal("1 already up to date (version label mismatch) · 1 version you skipped · 2 excluded programs",
            SoftwareUpdateFilter.Summary(hidden, excludedNotOffered: 2));
        Assert.Null(SoftwareUpdateFilter.Summary([]));
    }
}
