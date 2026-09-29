using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class InstallationFileRulesTests
{
    [Theory]
    [InlineData(@"C:\Users\K\Downloads\VLC-3.0.20-win64.msi", "Windows Installer package")]
    [InlineData(@"C:\Users\K\Downloads\App.msixbundle", "app package")]
    [InlineData(@"C:\Users\K\Downloads\npp.8.6.Installer.x64.exe", "setup program (file name)")]
    [InlineData(@"C:\Users\K\Downloads\ChromeSetup.exe", "setup program (file name)")]
    [InlineData(@"C:\Users\K\Downloads\tool-setup.zip", "setup archive")]
    public void RecognisesSetupFiles(string path, string expected)
    {
        Assert.Equal(expected, InstallationFileRules.InstallerReason(path, null));
    }

    [Theory]
    [InlineData(@"C:\Users\K\Downloads\photos.zip")]
    [InlineData(@"C:\Users\K\Downloads\report.pdf")]
    [InlineData(@"C:\Users\K\Downloads\putty-portable.exe")]
    [InlineData(@"C:\Users\K\Downloads\unins000.exe")]
    [InlineData(@"C:\Users\K\Desktop\game.exe")]
    public void IgnoresOtherFiles(string path)
    {
        Assert.Null(InstallationFileRules.InstallerReason(path, null));
    }

    [Fact]
    public void DescriptionMarksAnExeAsSetup()
    {
        Assert.NotNull(InstallationFileRules.InstallerReason(@"C:\Users\K\Desktop\x.exe", "Foo Setup"));
    }

    [Theory]
    [InlineData("Notepad++", "Notepad++ (64-bit x64)")]
    [InlineData("VLC media player", "VLC media player")]
    [InlineData("Google Chrome", "Google Chrome")]
    [InlineData("7-Zip 23.01", "7-Zip 23.01 (x64)")]
    public void MatchesInstalledPrograms(string guess, string installed)
    {
        Assert.Equal(installed, InstallationFileRules.MatchInstalled(guess, new[] { "Other App", installed }));
    }

    [Theory]
    [InlineData("vlc")]          // too short to trust
    [InlineData("Chrome")]       // not the start of "Google Chrome"
    [InlineData("Paint.NET")]
    public void DoesNotGuess(string guess)
    {
        Assert.Null(InstallationFileRules.MatchInstalled(guess, new[] { "VLC media player", "Google Chrome", "Notepad++" }));
    }
}

public class RedundantFileRulesTests
{
    [Theory]
    [InlineData("Cache", null, "cache")]
    [InlineData("Code Cache", null, "cache")]
    [InlineData("GPUCache", null, "cache")]
    [InlineData("cache2", null, "cache")]
    [InlineData("logs", null, "logs")]
    [InlineData("Crashes", null, "crash reports")]
    [InlineData("reports", "Crashpad", "crash reports")]
    [InlineData("reports", "Documents", null)]
    [InlineData("Temp", null, "temporary files")]
    [InlineData("Profiles", null, null)]
    [InlineData("User Data", null, null)]
    public void KindOf(string folder, string? parent, string? expected)
    {
        Assert.Equal(expected, RedundantFileRules.KindOf(folder, parent));
    }

    [Theory]
    [InlineData("Microsoft", true)]
    [InlineData("Packages", true)]
    [InlineData("Evict", true)]
    [InlineData("Google", false)]   // Chrome's cache lives here
    [InlineData("Mozilla", false)]
    public void SkippedRoots(string name, bool expected)
    {
        Assert.Equal(expected, RedundantFileRules.IsSkippedRoot(name));
    }

    private static readonly (string, string?)[] Installed =
    {
        ("Google Chrome", "Google LLC"), ("Google Drive", "Google LLC"), ("Microsoft Visual Studio Code", "Microsoft Corporation"),
        ("Discord", "Discord Inc."), ("Mozilla Firefox (x64 en-US)", "Mozilla"),
    };

    [Theory]
    [InlineData(new[] { "Google", "Chrome", "User Data", "Default" }, "Google Chrome")]
    [InlineData(new[] { "Google", "DriveFS", "logs" }, "Google")]          // ambiguous vendor → labelled by vendor
    [InlineData(new[] { "discord", "Cache" }, "Discord")]
    [InlineData(new[] { "Code", "CachedData" }, "Microsoft Visual Studio Code")]
    [InlineData(new[] { "Mozilla", "Firefox", "Profiles" }, "Mozilla Firefox (x64 en-US)")]
    public void OwnerOf_FindsInstalledProgram(string[] segments, string expected)
    {
        Assert.Equal(expected, RedundantFileRules.OwnerOf(segments, Installed));
    }

    [Fact]
    public void OwnerOf_NullForUninstalledSoftware()
    {
        Assert.Null(RedundantFileRules.OwnerOf(new[] { "Spotify", "Data" }, Installed));
        Assert.Null(RedundantFileRules.OwnerOf(new[] { "ab", "cd" }, Installed));
    }
}

public class UninstallIssueRulesTests
{
    private static Evict.Core.Models.InstalledProgram P(string? uninstall, string? folder, bool msi = false) => new()
    {
        Id = "Foo", DisplayName = "Foo", KeyName = "Foo", Scope = Evict.Core.Models.RegistryScope.User, RegistryPath = "HKCU", UninstallString = uninstall, InstallLocation = folder, IsMsi = msi,
    };

    private static readonly HashSet<string> Disk = new(StringComparer.OrdinalIgnoreCase) { @"C:\Foo", @"C:\Foo\unins000.exe", @"C:\Bar" };
    private static UninstallIssue Classify(Evict.Core.Models.InstalledProgram p) => UninstallIssueRules.Classify(p, Disk.Contains, Disk.Contains);

    [Fact] public void Healthy() => Assert.Equal(UninstallIssue.None, Classify(P(@"""C:\Foo\unins000.exe"" /SILENT", @"C:\Foo")));
    [Fact] public void Broken() => Assert.Equal(UninstallIssue.Broken, Classify(P(@"C:\Gone\unins000.exe", @"C:\Gone")));
    [Fact] public void BrokenWithoutFolderValue() => Assert.Equal(UninstallIssue.Broken, Classify(P(@"C:\Gone\unins000.exe", null)));
    [Fact] public void UninstallerMissing() => Assert.Equal(UninstallIssue.UninstallerMissing, Classify(P(@"C:\Bar\uninstall.exe", @"C:\Bar")));
    [Fact] public void NoUninstaller() => Assert.Equal(UninstallIssue.NoUninstaller, Classify(P(null, @"C:\Bar")));
    [Fact] public void NoUninstallerAndNoFolder_IsBroken() => Assert.Equal(UninstallIssue.Broken, Classify(P(null, null)));
    [Fact] public void MsiIsNeverAnIssue() => Assert.Equal(UninstallIssue.None, Classify(P(null, null, msi: true)));
    [Fact] public void RundllAndMsiexecAreNotJudged()
    {
        Assert.Equal(UninstallIssue.None, Classify(P(@"C:\Windows\system32\rundll32.exe C:\Gone\x.dll,Uninstall", @"C:\Bar")));
        Assert.Equal(UninstallIssue.None, Classify(P(@"MsiExec.exe /X{12345678-1234-1234-1234-123456789012}", null)));
    }

    [Fact]
    public void FailedBefore_OnlyNewerFailuresOfTheSameProgram()
    {
        var p = P(@"C:\Foo\unins000.exe", @"C:\Foo");
        p.InstallDate = new DateTime(2026, 1, 1);
        var failed = new Evict.Core.Models.UninstallHistoryEntry { ProgramName = "foo", Succeeded = false, Timestamp = new DateTime(2026, 2, 1) };
        var old = new Evict.Core.Models.UninstallHistoryEntry { ProgramName = "Foo", Succeeded = false, Timestamp = new DateTime(2025, 12, 1) };
        var ok = new Evict.Core.Models.UninstallHistoryEntry { ProgramName = "Foo", Succeeded = true, Timestamp = new DateTime(2026, 3, 1) };
        Assert.True(UninstallIssueRules.FailedBefore(p, new[] { failed }));
        Assert.False(UninstallIssueRules.FailedBefore(p, new[] { old, ok }));
    }
}

public class NotificationRulesTests
{
    private static readonly string[] Bloat = { "king.com.CandyCrush", "Microsoft.BingWeather" };

    [Theory]
    [InlineData("Windows.SystemToast.SecurityAndMaintenance", Evict.Core.Services.NotificationSenderKind.Essential)]
    [InlineData("Windows.Defender.SecurityCenter", Evict.Core.Services.NotificationSenderKind.Essential)]
    [InlineData("Windows.SystemToast.Suggested", Evict.Core.Services.NotificationSenderKind.Promotional)]
    [InlineData("Microsoft.Getstarted_8wekyb3d8bbwe!App", Evict.Core.Services.NotificationSenderKind.Promotional)]
    [InlineData("king.com.CandyCrushSaga_kgqvnymyfvs32!App", Evict.Core.Services.NotificationSenderKind.Promotional)]
    [InlineData("Microsoft.BingWeather_8wekyb3d8bbwe!App", Evict.Core.Services.NotificationSenderKind.Promotional)]
    [InlineData("MSEdge", Evict.Core.Services.NotificationSenderKind.Normal)]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Zoom\bin\Zoom.exe", Evict.Core.Services.NotificationSenderKind.Normal)]
    public void Classify(string id, Evict.Core.Services.NotificationSenderKind expected)
    {
        Assert.Equal(expected, Evict.Core.Services.NotificationRules.Classify(id, Bloat));
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore_8wekyb3d8bbwe!App", "Microsoft.WindowsStore")]
    [InlineData("Windows.SystemToast.SecurityAndMaintenance", "Windows – Security And Maintenance")]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Zoom\bin\Zoom.exe", "Zoom")]
    [InlineData("Chrome", "Chrome")]
    public void DisplayName(string id, string expected)
    {
        Assert.Equal(expected, Evict.Core.Services.NotificationRules.DisplayName(id));
    }

    [Fact]
    public void ResolvePath_MapsKnownFolders()
    {
        string Folder(Environment.SpecialFolder f) => f == Environment.SpecialFolder.ProgramFiles ? @"C:\Program Files" : "";
        Assert.Equal(Path.Combine(@"C:\Program Files", @"Zoom\bin\Zoom.exe"),
            Evict.Core.Services.NotificationRules.ResolvePath(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Zoom\bin\Zoom.exe", Folder));
        Assert.Null(Evict.Core.Services.NotificationRules.ResolvePath("MSEdge", Folder));
        Assert.Equal(@"C:\Tools\x.exe", Evict.Core.Services.NotificationRules.ResolvePath(@"C:\Tools\x.exe", Folder));
    }
}

public class PermissionRulesTests
{
    [Theory]
    [InlineData("webcam", "Camera")]
    [InlineData("microphone", "Microphone")]
    [InlineData("somethingNew", "somethingNew")]
    public void CapabilityName(string cap, string expected) => Assert.Equal(expected, Evict.Core.Services.PermissionRules.CapabilityName(cap));

    [Fact]
    public void SensitiveCapabilities()
    {
        Assert.True(Evict.Core.Services.PermissionRules.IsSensitive("webcam"));
        Assert.True(Evict.Core.Services.PermissionRules.IsSensitive("LOCATION"));
        Assert.False(Evict.Core.Services.PermissionRules.IsSensitive("musicLibrary"));
    }

    [Fact]
    public void NonPackagedPath() =>
        Assert.Equal(@"C:\Program Files\Zoom\bin\Zoom.exe", Evict.Core.Services.PermissionRules.NonPackagedPath("C:#Program Files#Zoom#bin#Zoom.exe"));

    [Fact]
    public void FileTimes()
    {
        Assert.Null(Evict.Core.Services.PermissionRules.FromFileTime(0));
        var t = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(t.ToLocalTime(), Evict.Core.Services.PermissionRules.FromFileTime(t.ToFileTimeUtc()));
    }

    [Theory]
    [InlineData("Allow", true)]
    [InlineData("deny", false)]
    [InlineData(null, true)]
    [InlineData("Prompt", true)]
    public void IsAllowed(string? v, bool expected) => Assert.Equal(expected, Evict.Core.Services.PermissionRules.IsAllowed(v));
}
