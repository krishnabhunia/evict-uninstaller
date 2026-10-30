using System.Text.Json.Nodes;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Version picker (design VP1): list every newer and older release, never choose for the user, roll back safely.</summary>
public sealed class VersionPickerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "evict-vp1-" + Guid.NewGuid().ToString("N"));

    public VersionPickerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ } }

    private static ReleaseInfo R(string tag, bool prerelease = false, string? published = null, bool withAssets = true)
    {
        var v = UpdateVersion.Parse(tag)!;
        var label = v.ToString();
        var assets = withAssets
            ? new List<ReleaseAsset>
            {
                new("Evict.exe", $"https://example.invalid/{tag}/Evict.exe", 1000),
                new("Evict.exe.sha256", $"https://example.invalid/{tag}/Evict.exe.sha256", 64),
                new($"Evict-Setup-{label}.exe", $"https://example.invalid/{tag}/setup.exe", 2000),
                new($"Evict-Setup-{label}.exe.sha256", $"https://example.invalid/{tag}/setup.exe.sha256", 64),
            }
            : new List<ReleaseAsset> { new("Evict.exe", $"https://example.invalid/{tag}/Evict.exe", 1000) };
        return new ReleaseInfo(v.Core, tag, tag, "https://example.invalid/" + tag, null,
            published is null ? null : DateTimeOffset.Parse(published), prerelease || v.IsPrerelease, assets);
    }

    private static readonly ReleaseInfo[] Sample =
    {
        R("win-v4.2.0-beta.110.40.1"),
        R("win-v4.1.1"),
        R("win-v4.2.0-beta.105.38.1"),
        R("win-v4.1.0"),
        R("win-v4.1.0-beta.101.32.1"),
        R("win-v4.0.1"),
        R("win-v4.0.0"),
        R("v1.3.3"),
        R("win-v1.3.3"),
    };

    // ───────────── lists ─────────────

    [Fact]
    public void NewerListsEveryStableAndBetaReleaseNewestFirst()
    {
        var newer = VersionPicker.Newer(Sample, "4.1.0");
        Assert.Equal(new[] { "4.2.0-beta.110.40.1", "4.2.0-beta.105.38.1", "4.1.1" }, newer.Select(r => r.DisplayVersion));
    }

    [Fact]
    public void OlderListsEveryEarlierReleaseIncludingBetasAndOneEntryPerVersion()
    {
        var older = VersionPicker.Older(Sample, "4.1.0");
        Assert.Equal(new[] { "4.1.0-beta.101.32.1", "4.0.1", "4.0.0", "1.3.3" }, older.Select(r => r.DisplayVersion));
        Assert.Equal("win-v1.3.3", older.Last().TagName); // the win-v tag wins over the legacy v tag
    }

    [Fact]
    public void ARunningBetaSeesItsFinalReleaseAsNewer()
    {
        Assert.Contains(VersionPicker.Newer(Sample, "4.1.0-beta.101.32.1"), r => r.DisplayVersion == "4.1.0");
        Assert.DoesNotContain(VersionPicker.Newer(Sample, "4.1.0-beta.101.32.1"), r => r.DisplayVersion == "4.1.0-beta.101.32.1");
    }

    [Fact]
    public void UnknownCurrentVersionListsNothing()
    {
        Assert.Empty(VersionPicker.Newer(Sample, "local build"));
        Assert.Empty(VersionPicker.Older(Sample, ""));
    }

    [Theory]
    [InlineData(VersionFilter.All, 3)]
    [InlineData(VersionFilter.Stable, 1)]
    [InlineData(VersionFilter.Beta, 2)]
    public void FilterSplitsStableAndBeta(VersionFilter filter, int count) =>
        Assert.Equal(count, VersionPicker.Filter(VersionPicker.Newer(Sample, "4.1.0"), filter).Count);

    // ───────────── the notice ─────────────

    [Fact]
    public void NoticeCountsOnlyTheChosenChannelAndNothingWhilePinned()
    {
        Assert.Single(VersionPicker.Offered(Sample, "4.1.0", includeBeta: false, pinnedVersion: null));
        Assert.Equal(3, VersionPicker.Offered(Sample, "4.1.0", includeBeta: true, pinnedVersion: null).Count);
        Assert.Empty(VersionPicker.Offered(Sample, "4.0.0", includeBeta: true, pinnedVersion: "4.0.0"));
    }

    [Fact]
    public void NoticeTextSaysHowManyOfEachKind()
    {
        Assert.Equal("3 newer versions found (1 stable, 2 beta)", VersionPicker.OfferText(VersionPicker.Newer(Sample, "4.1.0")));
        Assert.Equal("1 newer version found (1 stable)", VersionPicker.OfferText(new[] { R("win-v4.1.1") }));
        Assert.Equal("", VersionPicker.OfferText(Array.Empty<ReleaseInfo>()));
    }

    // ───────────── tags and warnings ─────────────

    [Fact]
    public void TagsMarkNewestLatestStableAndPreviousVersion()
    {
        var newer = VersionPicker.Newer(Sample, "4.1.0");
        Assert.Equal("Newest", VersionPicker.Tag(newer[0], newer, null));
        Assert.Equal("Latest stable", VersionPicker.Tag(newer.Single(r => r.DisplayVersion == "4.1.1"), newer, null));
        Assert.Null(VersionPicker.Tag(newer[1], newer, null));

        var older = VersionPicker.Older(Sample, "4.1.0");
        Assert.Equal("Your previous version", VersionPicker.Tag(older.Single(r => r.DisplayVersion == "4.0.1"), older, "4.0.1"));
    }

    [Fact]
    public void ChoosingABetaOverANewerStableWarnsThatTheStableIsSkipped()
    {
        var beta = Sample.Single(r => r.DisplayVersion == "4.2.0-beta.110.40.1");
        var warning = VersionChange.Warning(new VersionChangeRequest(beta, "4.1.0", InstalledMode: true), Sample);
        Assert.Contains("skips the latest stable (v4.1.1)", warning);

        var stable = Sample.Single(r => r.DisplayVersion == "4.1.1");
        Assert.Null(VersionChange.Warning(new VersionChangeRequest(stable, "4.1.0", InstalledMode: true), Sample));
    }

    [Fact]
    public void RollBackWarnsThatNewerFeaturesWillBeMissing()
    {
        var target = Sample.Single(r => r.DisplayVersion == "4.0.1");
        var request = new VersionChangeRequest(target, "4.1.0", InstalledMode: false);
        Assert.True(request.IsRollBack);
        Assert.Contains("older version", VersionChange.Warning(request, Sample));
    }

    [Fact]
    public void ReleasesWithoutAChecksumCannotBeInstalledByEvict()
    {
        Assert.True(VersionPicker.CanInstall(R("win-v4.0.0"), installedMode: true));
        Assert.False(VersionPicker.CanInstall(R("win-v1.0.0", withAssets: false), installedMode: false));
    }

    // ───────────── confirm step ─────────────

    [Fact]
    public void StepsDescribeTheRollBackIncludingSettingsAndPin()
    {
        var target = Sample.Single(r => r.DisplayVersion == "4.0.1");
        var request = new VersionChangeRequest(target, "4.1.0", InstalledMode: true, TargetOnThisPc: true, RestoreSettings: true, HasSettingsBackup: true, Pin: true);
        var steps = VersionChange.Steps(request);
        Assert.Contains(steps, s => s.Contains("Keep v4.1.0 on this PC"));
        Assert.Contains(steps, s => s.Contains("copy of v4.0.1 saved on this PC"));
        Assert.Contains(steps, s => s.Contains("Run its installer"));
        Assert.Contains(steps, s => s.Contains("Put back the settings you used with v4.0.1"));
        Assert.Contains(steps, s => s.Contains("Pin v4.0.1"));
    }

    [Fact]
    public void AnUpdateNeverPinsOrRestoresSettings()
    {
        var request = new VersionChangeRequest(R("win-v4.1.1"), "4.1.0", InstalledMode: false, RestoreSettings: true, HasSettingsBackup: true, Pin: true);
        Assert.False(request.WillPin);
        Assert.False(request.WillRestoreSettings);
        Assert.Contains(VersionChange.Steps(request), s => s.Contains("Download v4.1.1"));
    }

    [Fact]
    public void PinningTurnsTheStartupCheckOffAndChoosingAnUpdateEndsThePin()
    {
        var s = new AppSettings { SkippedUpdateVersion = "win-v4.1.1" };
        VersionChange.ApplyChoices(s, new VersionChangeRequest(R("win-v4.0.1"), "4.1.0", InstalledMode: true, Pin: true));
        Assert.Equal("4.0.1", s.PinnedVersion);
        Assert.False(s.CheckForUpdates);
        Assert.Equal("4.1.0", s.PreviousVersion);
        Assert.Null(s.SkippedUpdateVersion);

        VersionChange.ApplyChoices(s, new VersionChangeRequest(R("win-v4.1.1"), "4.0.1", InstalledMode: true));
        Assert.Null(s.PinnedVersion);
        Assert.True(s.CheckForUpdates);
        Assert.Equal("4.0.1", s.PreviousVersion);
    }

    // ───────────── versions kept on this PC ─────────────

    [Fact]
    public void KeptCopyIsFoundOnlyWhileItsChecksumStillMatches()
    {
        var archive = new VersionArchive(Path.Combine(_dir, "versions"));
        var source = Path.Combine(_dir, "Evict.exe");
        File.WriteAllText(source, "evict 4.1.0");
        archive.Keep("4.1.0", source, "Evict.exe", VersionArchive.Sha256(source));

        Assert.True(archive.Has("4.1.0", "Evict.exe"));
        Assert.NotNull(archive.Find("4.1.0", "Evict.exe"));
        Assert.Null(archive.Find("4.0.0", "Evict.exe"));

        File.WriteAllText(archive.Find("4.1.0", "Evict.exe")!, "tampered");
        Assert.Null(archive.Find("4.1.0", "Evict.exe"));
    }

    [Fact]
    public void SettingsOfAVersionArePutBackWithTheChangeCarriedOver()
    {
        var archive = new VersionArchive(Path.Combine(_dir, "versions"));
        var settingsFile = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settingsFile, """{ "Theme": "Dark", "CheckForUpdates": true }""");
        Assert.True(archive.BackUpSettings("4.0.1", settingsFile));
        File.WriteAllText(settingsFile, """{ "Theme": "Light", "NewerSetting": 7 }""");

        var carried = new Dictionary<string, JsonNode?> { ["PinnedVersion"] = "4.0.1", ["CheckForUpdates"] = false };
        Assert.True(archive.RestoreSettings("4.0.1", settingsFile, carried));

        var json = JsonNode.Parse(File.ReadAllText(settingsFile))!.AsObject();
        Assert.Equal("Dark", (string?)json["Theme"]);
        Assert.Equal("4.0.1", (string?)json["PinnedVersion"]);
        Assert.False((bool)json["CheckForUpdates"]!);
        Assert.False(json.ContainsKey("NewerSetting"));
        Assert.False(archive.RestoreSettings("3.0.0", settingsFile)); // nothing saved for that version
    }

    [Fact]
    public void PruneKeepsTheNewestCopiesAndTheProtectedOnes()
    {
        var root = Path.Combine(_dir, "versions");
        var archive = new VersionArchive(root);
        var source = Path.Combine(_dir, "Evict.exe");
        File.WriteAllText(source, "x");
        var hash = VersionArchive.Sha256(source);
        var versions = new[] { "1.0.0", "2.0.0", "3.0.0", "4.0.0", "5.0.0" };
        for (int i = 0; i < versions.Length; i++)
        {
            archive.Keep(versions[i], source, "Evict.exe", hash);
            Directory.SetLastWriteTimeUtc(Path.Combine(root, versions[i]), DateTime.UtcNow.AddMinutes(i));
        }

        archive.Prune(2, new[] { "1.0.0" });

        Assert.Equal(new[] { "1.0.0", "4.0.0", "5.0.0" }, archive.Versions().OrderBy(v => v));
    }

    // ───────────── settings and history ─────────────

    [Fact]
    public void SuspendedSavesLeaveThePutBackSettingsAlone()
    {
        var file = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(() => file);
        store.Current.Theme = "Dark";
        store.Save();
        store.SuspendSaves();
        store.Current.Theme = "Light";
        Assert.True(store.TrySave());
        Assert.Contains("Dark", File.ReadAllText(file));

        store.ResumeSaves();
        store.Save();
        Assert.Contains("Light", File.ReadAllText(file));
    }

    [Fact]
    public void RollBackIsRecordedAsDoneOnlyOnTheExactOlderVersion()
    {
        var ok = new SoftwareUpdateHistoryStore(null, () => DateTime.UtcNow);
        ok.BeginSelfUpdate("4.1.0", "4.0.1", "win-v4.0.1", "You");
        var entry = ok.ResolveSelfUpdate("4.0.1");
        Assert.Equal(SoftwareUpdateOutcome.Updated, entry!.Outcome);
        Assert.Contains("rolled back to 4.0.1", entry.Message);

        var failed = new SoftwareUpdateHistoryStore(null, () => DateTime.UtcNow);
        failed.BeginSelfUpdate("4.1.0", "4.0.1", "win-v4.0.1", "You");
        var still = failed.ResolveSelfUpdate("4.1.0");
        Assert.Equal(SoftwareUpdateOutcome.Failed, still!.Outcome);
        Assert.Contains("roll back did not finish", still.Message);
    }

    [Fact]
    public void OldSettingsFilesLoadWithTheNewVersionPickerDefaults()
    {
        var file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{ "Theme": "Dark" }""");
        var store = new SettingsStore(() => file);
        store.Load();
        Assert.Null(store.Current.PinnedVersion);
        Assert.Null(store.Current.PreviousVersion);
        Assert.True(store.Current.KeepVersionCopies);
    }

    // ───────────── review fixes ─────────────

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void ADamagedSettingsBackupNeverReplacesTheCurrentSettings(string damaged)
    {
        var archive = new VersionArchive(Path.Combine(_dir, "versions"));
        var settingsFile = Path.Combine(_dir, "settings.json");
        File.WriteAllText(settingsFile, """{ "Theme": "Dark" }""");
        Assert.True(archive.BackUpSettings("4.0.1", settingsFile));
        File.WriteAllText(archive.SettingsBackup("4.0.1")!, damaged);
        File.WriteAllText(settingsFile, """{ "Theme": "Light" }""");

        Assert.False(archive.RestoreSettings("4.0.1", settingsFile));
        Assert.Equal("""{ "Theme": "Light" }""", File.ReadAllText(settingsFile));
    }

    [Fact]
    public void AFailedChangeTakesBackThePinAndTheOtherChoices()
    {
        var s = new AppSettings { SkippedUpdateVersion = "win-v4.1.1", PreviousVersion = "3.0.0" };
        var before = VersionChange.Take(s);
        VersionChange.ApplyChoices(s, new VersionChangeRequest(R("win-v4.0.1"), "4.1.0", InstalledMode: false, KeepCurrentCopy: false, Pin: true));
        Assert.Equal("4.0.1", s.PinnedVersion);

        VersionChange.Restore(s, before);

        Assert.Null(s.PinnedVersion);
        Assert.True(s.CheckForUpdates);
        Assert.Equal("win-v4.1.1", s.SkippedUpdateVersion);
        Assert.Equal("3.0.0", s.PreviousVersion);
        Assert.True(s.KeepVersionCopies);
    }

    [Fact]
    public void KeptVersionsAreListedOfflineWithTheirDateAndCanBeRolledBackTo()
    {
        var archive = new VersionArchive(Path.Combine(_dir, "versions"));
        var source = Path.Combine(_dir, "setup.exe");
        File.WriteAllText(source, "setup 4.0.1");
        var release = R("win-v4.0.1", published: "2026-10-28T06:40:07Z");
        archive.Keep("4.0.1", source, "Evict-Setup-4.0.1.exe", VersionArchive.Sha256(source), release);
        Directory.CreateDirectory(Path.Combine(_dir, "versions", "4.0.0")); // settings only: not listed

        var kept = archive.KeptReleases();

        var only = Assert.Single(kept);
        Assert.Equal("4.0.1", only.DisplayVersion);
        Assert.Equal("win-v4.0.1", only.TagName);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T06:40:07Z"), only.PublishedAt);
        Assert.True(VersionPicker.CanInstall(only, installedMode: true));
        Assert.Contains(VersionPicker.Older(VersionPicker.WithKept(Array.Empty<ReleaseInfo>(), kept), "4.1.0"), r => r.DisplayVersion == "4.0.1");
    }

    [Fact]
    public void GitHubsEntryWinsOverTheKeptOneForTheSameVersion()
    {
        var kept = new[] { R("win-v4.0.1", published: "2020-01-01T00:00:00Z"), R("win-v3.0.0") };
        var merged = VersionPicker.WithKept(Sample, kept);
        Assert.Same(Sample.Single(r => r.TagName == "win-v4.0.1"), merged.Single(r => r.DisplayVersion == "4.0.1"));
        Assert.Contains(merged, r => r.DisplayVersion == "3.0.0");
    }
}
