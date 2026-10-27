using System.Text.Json;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "EvictSettingsTests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsPersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void SuccessfulSavePublishesACompleteDocumentAndCanBeLoadedAgain()
    {
        var store = new SettingsStore(() => SettingsPath);
        store.Current.ScheduledScan = "Weekly";
        store.Current.ScheduledScanHour = 17;
        store.Current.ScheduledScanWeekday = 4;
        store.Current.Theme = "Dark";

        Assert.True(store.TrySave());
        using (var json = JsonDocument.Parse(File.ReadAllText(SettingsPath)))
            Assert.Equal("Weekly", json.RootElement.GetProperty("ScheduledScan").GetString());

        var reloaded = new SettingsStore(() => SettingsPath);
        reloaded.Load();
        Assert.Equal("Weekly", reloaded.Current.ScheduledScan);
        Assert.Equal(17, reloaded.Current.ScheduledScanHour);
        Assert.Equal(4, reloaded.Current.ScheduledScanWeekday);
        Assert.Equal("Dark", reloaded.Current.Theme);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedPublicationLeavesPreviousBytesAndUnrelatedFilesIntact()
    {
        var original = new SettingsStore(() => SettingsPath);
        original.Current.ScheduledScan = "Off";
        Assert.True(original.TrySave());
        var before = File.ReadAllBytes(SettingsPath);
        var unrelated = Path.Combine(_directory, "unrelated.tmp");
        File.WriteAllText(unrelated, "keep me");
        var publishCalls = 0;
        var store = new SettingsStore(() => SettingsPath, (staged, destination) =>
        {
            publishCalls++;
            Assert.Equal(_directory, Path.GetDirectoryName(staged));
            Assert.Equal(SettingsPath, destination);
            using var json = JsonDocument.Parse(File.ReadAllText(staged));
            Assert.Equal("Daily", json.RootElement.GetProperty("ScheduledScan").GetString());
            Assert.Equal(before, File.ReadAllBytes(destination));
            throw new IOException("Simulated sharing violation while publishing settings.");
        });
        store.Load();
        store.Current.ScheduledScan = "Daily";

        Assert.False(store.TrySave());
        Assert.Equal(1, publishCalls);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Equal("keep me", File.ReadAllText(unrelated));
        Assert.Equal(2, Directory.GetFiles(_directory).Length);
        var reloaded = new SettingsStore(() => SettingsPath);
        reloaded.Load();
        Assert.Equal("Off", reloaded.Current.ScheduledScan);
    }

    [Fact]
    public void FailedFirstPublicationDoesNotLeaveAPartialSettingsFile()
    {
        var store = new SettingsStore(() => SettingsPath, (_, _) => throw new IOException("Disk unavailable."));
        Assert.False(store.TrySave());
        Assert.False(File.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ReplacingExistingSettingsKeepsTheLatestCompleteDocument()
    {
        var store = new SettingsStore(() => SettingsPath);
        store.Current.ScheduledScan = "Daily";
        Assert.True(store.TrySave());
        store.Current.ScheduledScan = "Off";
        Assert.True(store.TrySave());

        var reloaded = new SettingsStore(() => SettingsPath);
        reloaded.Load();
        Assert.Equal("Off", reloaded.Current.ScheduledScan);
        Assert.Single(Directory.GetFiles(_directory));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
