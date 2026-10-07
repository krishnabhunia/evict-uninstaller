using System.Text.Json;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class UpdateChannelSettingsTests
{
    [Fact]
    public void Missing_beta_preference_stays_on_stable_releases()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        SettingsStore.Migrate(settings);
        Assert.False(settings.IncludeBetaUpdates);
    }

    [Fact]
    public void Beta_opt_in_survives_serialization_and_settings_migration()
    {
        var saved = JsonSerializer.Serialize(new AppSettings { IncludeBetaUpdates = true });
        var settings = JsonSerializer.Deserialize<AppSettings>(saved)!;
        SettingsStore.Migrate(settings);
        Assert.True(settings.IncludeBetaUpdates);
        Assert.False(new AppSettings().IncludeBetaUpdates);
    }
}
