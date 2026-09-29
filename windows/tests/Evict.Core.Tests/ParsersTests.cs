using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public class ParsersTests
{
    private const string WingetSample = """
        Name                                   Id                                Version        Available      Source
        -----------------------------------------------------------------------------------------------------------
        7-Zip 23.01 (x64)                      7zip.7zip                         23.01          24.09          winget
        Microsoft Visual Studio Code (User)    Microsoft.VisualStudioCode        1.92.0         1.93.1         winget
        Notepad++ (64-bit x64)                 Notepad++.Notepad++               8.6.9          8.7            winget
        Python 3.12.4 (64-bit)                 Python.Python.3.12                3.12.4         3.12.6         winget
        4 upgrades available.
        """;

    [Fact]
    public void Winget_ParsesTable()
    {
        var rows = WingetService.ParseUpgradeTable(WingetSample);
        Assert.Equal(4, rows.Count);
        Assert.Equal("7zip.7zip", rows[0].Id);
        Assert.Equal("7-Zip 23.01 (x64)", rows[0].Name);
        Assert.Equal("23.01", rows[0].InstalledVersion);
        Assert.Equal("24.09", rows[0].AvailableVersion);
        Assert.Equal("winget", rows[0].Source);
        Assert.Equal("Notepad++.Notepad++", rows[2].Id);
        Assert.Equal("Python.Python.3.12", rows[3].Id);
    }

    [Fact]
    public void Winget_HandlesSpinnerAndCarriageReturns()
    {
        var text = "   -\r   \\\r   |\r" + WingetSample.Replace("\n", "\r\n");
        var rows = WingetService.ParseUpgradeTable(text);
        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public void Winget_EmptyOrNoHeader()
    {
        Assert.Empty(WingetService.ParseUpgradeTable(""));
        Assert.Empty(WingetService.ParseUpgradeTable("No installed package found matching input criteria."));
    }

    [Theory]
    [InlineData("Microsoft.WindowsSoundRecorder", "Windows Sound Recorder")]
    [InlineData("Microsoft.ZuneMusic", "Zune Music")]
    [InlineData("king.com.CandyCrushSaga", "com Candy Crush Saga")]
    [InlineData("Clipchamp.Clipchamp", "Clipchamp")]
    public void Appx_FriendlyName(string pkg, string expected) => Assert.Equal(expected, AppxService.FriendlyName(pkg));

    [Fact]
    public void Appx_PublisherOrganization()
    {
        Assert.Equal("Microsoft Corporation", AppxService.PublisherOrganization("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("Contoso Ltd", AppxService.PublisherOrganization("CN=Contoso Ltd"));
        Assert.Null(AppxService.PublisherOrganization(null));
    }

    [Fact]
    public void Chromium_WebkitTime()
    {
        // 13300000000000000 µs since 1601 → 2022-06-12ish
        var dt = BrowserExtensionService.WebkitTimeToDateTime(13300000000000000).GetValueOrDefault().ToUniversalTime();
        Assert.Equal(2022, dt.Year);
        Assert.Null(BrowserExtensionService.WebkitTimeToDateTime(0));
    }

    [Fact]
    public void Chromium_ParseEntry()
    {
        const string json = """
            {
              "from_webstore": true,
              "install_time": "13300000000000000",
              "location": 1,
              "state": 1,
              "path": "abcdefghijklmnopabcdefghijklmnop\\1.2.3_0",
              "manifest": { "name": "uBlock Origin", "version": "1.2.3", "description": "Blocker", "permissions": ["tabs", "storage"], "homepage_url": "https://example.org" }
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var profile = Path.Combine(Path.GetTempPath(), "evict-test-profile");
        var info = BrowserExtensionService.ParseChromiumEntry(BrowserKind.Chrome, profile, "Default", "abcdefghijklmnopabcdefghijklmnop", doc.RootElement);
        Assert.NotNull(info);
        Assert.Equal("uBlock Origin", info!.Name);
        Assert.Equal("1.2.3", info.Version);
        Assert.True(info.Enabled);
        Assert.True(info.FromWebStore);
        Assert.False(info.InstalledByPolicy);
        Assert.Equal("Web Store", info.Source);
        Assert.Equal(2, info.Permissions.Count);
        Assert.Equal(Path.Combine(profile, "Extensions", "abcdefghijklmnopabcdefghijklmnop"), info.ExtensionPath);
        Assert.Equal(2022, info.InstallTime!.Value.ToUniversalTime().Year);
    }

    [Fact]
    public void Chromium_PolicyAndComponentFlags()
    {
        using var doc = JsonDocument.Parse("""{ "location": 9, "state": 1, "manifest": { "name": "Corp Ext", "version": "1" } }""");
        var info = BrowserExtensionService.ParseChromiumEntry(BrowserKind.Edge, "C:\\p", "Default", "abcdefghijklmnopabcdefghijklmnop", doc.RootElement)!;
        Assert.True(info.InstalledByPolicy);
        Assert.Equal("Policy", info.Source);

        using var doc2 = JsonDocument.Parse("""{ "location": 5, "state": 1, "manifest": { "name": "Built in", "version": "1" } }""");
        var info2 = BrowserExtensionService.ParseChromiumEntry(BrowserKind.Edge, "C:\\p", "Default", "abcdefghijklmnopabcdefghijklmnop", doc2.RootElement)!;
        Assert.True(info2.IsComponent);
        Assert.Equal("Built-in", info2.Source);
    }
}
