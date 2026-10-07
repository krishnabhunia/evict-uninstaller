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

public class SecurityRulesTests
{
    [Fact]
    public void ParseDefender_ReadsStatusThreatsAndOtherAv()
    {
        var d = Evict.Core.Services.SecurityRules.ParseDefender(
            """{"AntivirusEnabled":true,"RealTime":false,"SigAge":9,"QuickScanAge":40,"Mode":"Normal","Threats":["Trojan:Win32/X"],"OtherAv":["Windows Defender"]}""");
        Assert.True(d.AntivirusEnabled);
        Assert.False(d.RealTimeProtection);
        Assert.Equal(9, d.SignatureAgeDays);
        Assert.Single(d.ActiveThreats);
        Assert.Empty(d.OtherAntivirus); // Defender itself is not "another" antivirus
        var f = Evict.Core.Services.SecurityRules.DefenderFindings(d).ToList();
        Assert.Contains(f, x => x.Title.StartsWith("Active threat") && x.Severity == Evict.Core.Services.FindingSeverity.High);
        Assert.Contains(f, x => x.Title == "Real-time protection is off");
        Assert.Contains(f, x => x.Title.StartsWith("Virus definitions are 9 days old"));
        Assert.Contains(f, x => x.ActionKey == "quickscan");
    }

    [Fact]
    public void ParseDefender_SingleStringsAndOtherAntivirus()
    {
        var d = Evict.Core.Services.SecurityRules.ParseDefender("""{"Threats":"Adware:X","OtherAv":"Bitdefender","Error":"passive"}""");
        Assert.Equal(new[] { "Adware:X" }, d.ActiveThreats);
        Assert.Equal(new[] { "Bitdefender" }, d.OtherAntivirus);
        var f = Evict.Core.Services.SecurityRules.DefenderFindings(d).ToList();
        Assert.Contains(f, x => x.Title.StartsWith("Registered antivirus: Bitdefender"));
        Assert.Contains(f, x => x.Title == "Antivirus status unknown");
        Assert.DoesNotContain(f, x => x.Title.Contains("protection healthy"));
        Assert.DoesNotContain(f, x => x.Title == "Real-time protection is off");
    }

    [Fact]
    public void ParseDefender_Garbage()
    {
        Assert.NotNull(Evict.Core.Services.SecurityRules.ParseDefender("not json").Error);
        Assert.NotNull(Evict.Core.Services.SecurityRules.ParseDefender(null).Error);
        Assert.Contains(Evict.Core.Services.SecurityRules.DefenderFindings(Evict.Core.Services.SecurityRules.ParseDefender(null)), x => x.Title == "Antivirus status unknown");
    }

    private static Evict.Core.Models.BrowserExtensionInfo Ext(bool store, bool policy, bool component = false) => new()
    {
        Browser = Evict.Core.Models.BrowserKind.Chrome, ProfileName = "Default", ProfilePath = @"C:\p", ExtensionId = "abc", Name = "X",
        FromWebStore = store, InstalledByPolicy = policy, IsComponent = component,
    };

    [Fact]
    public void ExtensionFindings()
    {
        Assert.Null(Evict.Core.Services.SecurityRules.ExtensionFinding(Ext(store: true, policy: false)));
        Assert.Null(Evict.Core.Services.SecurityRules.ExtensionFinding(Ext(store: false, policy: false, component: true)));
        Assert.Contains("not installed from the web store", Evict.Core.Services.SecurityRules.ExtensionFinding(Ext(false, false))!.Title);
        Assert.Contains("forced on by a policy", Evict.Core.Services.SecurityRules.ExtensionFinding(Ext(true, true))!.Title);
    }

    [Fact]
    public void UserWritableLocations()
    {
        var roots = new[] { @"C:\Users\K\AppData\Roaming", @"C:\Users\K\AppData\Local\Temp" };
        Assert.True(Evict.Core.Services.SecurityRules.IsUserWritableLocation(@"C:\Users\K\AppData\Roaming\x\y.exe", roots));
        Assert.False(Evict.Core.Services.SecurityRules.IsUserWritableLocation(@"C:\Program Files\x\y.exe", roots));
        Assert.False(Evict.Core.Services.SecurityRules.IsUserWritableLocation(null, roots));
    }
}

public class HibernationRulesTests
{
    [Theory]
    [InlineData("gupdate", "Google Update Service (gupdate)", true)]
    [InlineData("AdobeARMservice", "Adobe Acrobat Update Service", true)]
    [InlineData("MozillaMaintenance", "Mozilla Maintenance Service", true)]
    [InlineData("Steam Client Service", "Steam Client Service", false)]
    public void IsUpdater(string name, string display, bool expected) => Assert.Equal(expected, Evict.Core.Services.HibernationRules.IsUpdater(name, display));

    [Theory]
    [InlineData("NVIDIA Display Container LS")]
    [InlineData("Bitdefender Endpoint Security")]
    [InlineData("OpenVPN Interactive Service")]
    [InlineData("Realtek Audio Universal Service")]
    [InlineData("Dropbox Update Service")]      // sync client: updater, but its tasks keep sync healthy
    public void Protected(string text) => Assert.True(Evict.Core.Services.HibernationRules.IsProtected(text));

    [Fact]
    public void NotProtected() => Assert.False(Evict.Core.Services.HibernationRules.IsProtected("Google Update Service", "Google LLC"));

    [Theory]
    [InlineData(0x10, 2, @"C:\Program Files\Google\Update\GoogleUpdate.exe", "Google LLC", true)]
    [InlineData(0x20, 2, @"C:\Program Files\X\x.exe", null, true)]
    [InlineData(0x10, 3, @"C:\Program Files\X\x.exe", null, false)]           // manual start – not always running
    [InlineData(0x1, 2, @"C:\Program Files\X\x.sys", null, false)]            // kernel driver
    [InlineData(0x10, 2, @"C:\Windows\System32\svchost.exe", null, false)]    // Windows' own
    [InlineData(0x10, 2, @"C:\Program Files\Microsoft\Edge\x.exe", "Microsoft Corporation", false)]
    [InlineData(0x10, 2, null, null, false)]
    public void CandidateService(int type, int start, string? image, string? company, bool expected) =>
        Assert.Equal(expected, Evict.Core.Services.HibernationRules.IsCandidateService(type, start, image, company, @"C:\Windows"));

    [Fact]
    public void ScStartArgument()
    {
        Assert.Equal("delayed-auto", Evict.Core.Services.HibernationRules.ScStartArgument(true));
        Assert.Equal("auto", Evict.Core.Services.HibernationRules.ScStartArgument(false));
    }

    [Fact]
    public void ParseTasks_ArrayObjectAndMicrosoftFolder()
    {
        var list = Evict.Core.Services.HibernationRules.ParseTasks(
            """[{"TaskPath":"\\","TaskName":"GoogleUpdateTaskMachineUA","State":"Ready","Execute":"C:\\Program Files (x86)\\Google\\Update\\GoogleUpdate.exe"},{"TaskPath":"\\Microsoft\\Windows\\Defrag\\","TaskName":"ScheduledDefrag","State":"Ready"},{"TaskPath":"\\Vendor\\","TaskName":"Helper","State":"Disabled"}]""");
        Assert.Equal(2, list.Count);
        Assert.True(list[0].Enabled);
        Assert.False(list[1].Enabled);
        Assert.Single(Evict.Core.Services.HibernationRules.ParseTasks("""{"TaskPath":"\\","TaskName":"One","State":"Ready"}"""));
        Assert.Empty(Evict.Core.Services.HibernationRules.ParseTasks("garbage"));
    }
}

public class LogFolderRulesTests
{
    [Fact]
    public void OnlyLogFiles()
    {
        Assert.True(RedundantFileRules.OnlyLogFiles(new[] { @"C:\a\app.log", @"C:\a\app.log.1", @"C:\a\trace.etl", @"C:\a\x.old" }));
        Assert.False(RedundantFileRules.OnlyLogFiles(new[] { @"C:\a\app.log", @"C:\a\chat-with-alice.txt" }));
        Assert.False(RedundantFileRules.OnlyLogFiles(new[] { @"C:\a\export.html" }));
        Assert.False(RedundantFileRules.OnlyLogFiles(Array.Empty<string>()));
    }
}
