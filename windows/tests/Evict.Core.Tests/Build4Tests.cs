using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class Build4Tests
{
    // ───────────── installer detection heuristics ─────────────

    [Theory]
    [InlineData(@"C:\Users\k\Downloads\npp.8.6.Installer.x64.exe", null, null)]
    [InlineData(@"C:\Users\k\Downloads\setup.exe", null, null)]
    [InlineData(@"C:\Users\k\Downloads\VSCodeUserSetup-x64-1.93.0.exe", null, null)]
    [InlineData(@"D:\tools\Foo_Setup_2.1.exe", null, null)]
    [InlineData(@"C:\Users\k\Downloads\python-3.12.6-amd64.exe", "Python 3.12.6 (64-bit) Installer", null)]
    [InlineData(@"C:\Users\k\AppData\Local\Temp\is-ABC12.tmp\vlc-3.0.21-win64.tmp", null, null)]
    [InlineData(@"C:\Users\k\AppData\Local\Temp\nsa1B2C.tmp\setup_helper.exe", null, null)]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, @"msiexec /i ""C:\x\a.msi"" /qn /forcerestart")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", "Windows® installer", @"msiexec.exe /i ""C:\Users\k\Downloads\7z2408-x64.msi""")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, @"msiexec /package C:\x\a.msi /qb")]
    [InlineData(@"C:\Users\k\Downloads\Something-2.4.1-win64.exe", null, null)]
    public void Classify_RecognisesInstallers(string path, string? description, string? commandLine)
    {
        Assert.NotNull(InstallerHeuristics.Classify(path, description, commandLine));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Notepad++\notepad++.exe", "Notepad++ : a free (GNU) source code editor", null)]
    [InlineData(@"C:\Program Files\Evict\Evict.exe", "Evict Uninstaller", null)]
    [InlineData(@"C:\Program Files\Foo\unins000.exe", "Setup/Uninstall", null)]
    [InlineData(@"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe", "Google Installer", null)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", null, null)]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, @"C:\Windows\system32\msiexec.exe /V")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, @"msiexec /x {GUID} /qn")]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, null)]
    [InlineData(@"C:\Windows\servicing\TrustedInstaller.exe", "Windows Modules Installer", null)]
    [InlineData(@"C:\Users\k\AppData\Local\Discord\Update.exe", "Discord updater", null)]
    [InlineData(@"C:\Users\k\Downloads\report.exe", null, null)]
    [InlineData(@"C:\Users\k\AppData\Local\Temp\~nsuA.tmp\Au_.exe", null, null)]
    [InlineData(@"C:\Windows\System32\msiexec.exe", null, @"msiexec /I{GUID} /fa")]
    [InlineData(@"C:\Users\k\Downloads\rufus-4.5.exe", "Rufus – reliable USB formatting utility", null)]
    [InlineData(@"C:\Users\k\Downloads\HWiNFO64_portable.exe", null, null)]
    [InlineData(@"C:\Users\HP\AppData\Local\Programs\Opera\assistant\assistant_installer.exe", "Opera Browser Assistant Installer", null)]
    [InlineData(@"C:\Program Files\Foo\setup.exe", null, null)]
    [InlineData("", null, null)]
    public void Classify_IgnoresOrdinaryProgramsAndUninstallers(string path, string? description, string? commandLine)
    {
        Assert.Null(InstallerHeuristics.Classify(path, description, commandLine));
    }

    [Theory]
    [InlineData(@"C:\d\npp.8.6.Installer.x64.exe", null, null, "npp 8 6")]
    [InlineData(@"C:\d\VSCodeUserSetup-x64-1.93.0.exe", "Microsoft Visual Studio Code Setup", "Visual Studio Code", "Visual Studio Code")]
    [InlineData(@"C:\d\setup.exe", null, null, "setup.exe")]
    [InlineData(@"C:\d\foo_setup.exe", "Windows Installer", null, "foo")]
    public void FriendlyName_PrefersProductThenDescriptionThenCleanedStem(string path, string? description, string? product, string expected)
    {
        Assert.Equal(expected, InstallerHeuristics.FriendlyName(path, description, product));
    }

    [Fact]
    public void FriendlyName_ForMsiexecUsesThePackageName()
    {
        Assert.Equal("7z2408-x64", InstallerHeuristics.FriendlyName(@"C:\Windows\System32\msiexec.exe", "Windows® installer", "Windows Installer - Unicode", @"msiexec.exe /i ""C:\Users\k\Downloads\7z2408-x64.msi"" /qb"));
    }

    [Fact]
    public void SplitArgs_RespectsQuotes()
    {
        var args = InstallerHeuristics.SplitArgs("\"C:\\Program Files\\a b\\x.exe\" /i \"C:\\d\\my pkg.msi\" /qn");
        Assert.Equal(new[] { @"C:\Program Files\a b\x.exe", "/i", @"C:\d\my pkg.msi", "/qn" }, args);
    }

    [Fact]
    public void ScheduledScan_TaskXmlIsWellFormedAndRunsOnBattery()
    {
        var xml = ScheduledScanTask.BuildTaskXml(@"C:\Tools\Evict & Co\Evict.exe", "Weekly", 9, 3, "S-1-5-21-1-2-3-1001");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.Contains("<Wednesday />", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
        Assert.Contains(@"C:\Tools\Evict &amp; Co\Evict.exe", xml);
        Assert.Contains("--scheduled-scan", xml);
        Assert.Contains("S-1-5-21-1-2-3-1001", xml);
        Assert.NotNull(doc.Root);
        var daily = ScheduledScanTask.BuildTaskXml("e.exe", "Daily", 22, 0, null);
        Assert.Contains("<ScheduleByDay>", daily);
        Assert.DoesNotContain("<UserId>", daily);
        Assert.Contains("T22:00:00", daily);
    }

    [Fact]
    public void ProcessTree_TracksDescendantsOnly()
    {
        var tree = new ProcessTree(100);
        tree.OnProcessCreated(200, 100);   // child
        tree.OnProcessCreated(300, 200);   // grandchild
        tree.OnProcessCreated(400, 999);   // unrelated
        Assert.True(tree.Contains(100));
        Assert.True(tree.Contains(200));
        Assert.True(tree.Contains(300));
        Assert.False(tree.Contains(400));
        Assert.Equal(3, tree.Count);
    }

    // ───────────── Windows Installer cache ─────────────

    [Fact]
    public void FindOrphans_KeepsReferencedPackagesAndPatches()
    {
        var files = new[] { @"C:\Windows\Installer\1a2b3c.msi", @"C:\Windows\Installer\4d5e6f.msp", @"C:\Windows\Installer\orphan.msi", @"C:\Windows\Installer\Orphan2.msp" };
        var referenced = new[] { @"C:\WINDOWS\Installer\1A2B3C.msi", @"c:\windows\installer\4d5e6f.msp", "" };
        var orphans = InstallerCacheLogic.FindOrphans(files, referenced);
        Assert.Equal(new[] { @"C:\Windows\Installer\orphan.msi", @"C:\Windows\Installer\Orphan2.msp" }, orphans);
    }

    [Fact]
    public void FindOrphans_MatchesByFileNameWhenDriveLetterDiffers()
    {
        var files = new[] { @"C:\Windows\Installer\abc.msi" };
        var referenced = new[] { @"D:\Windows\Installer\ABC.MSI" };
        Assert.Empty(InstallerCacheLogic.FindOrphans(files, referenced));
    }

    [Theory]
    [InlineData("a.msi", true)]
    [InlineData("A.MSP", true)]
    [InlineData("SourceHash{GUID}", false)]
    [InlineData("icon.exe", false)]
    public void IsCacheFile_OnlyMsiAndMsp(string name, bool expected) => Assert.Equal(expected, InstallerCacheLogic.IsCacheFile(name));

    // ───────────── scheduled scan ─────────────

    [Fact]
    public void ScheduledScan_BuildsWeeklyAndDailyArguments()
    {
        var weekly = ScheduledScanTask.BuildCreateArguments(@"C:\Tools\Evict\Evict.exe", "Weekly", 10, 0);
        Assert.Contains("/SC WEEKLY /D SUN", weekly);
        Assert.Contains("/XML", ScheduledScanTask.BuildCreateFromXmlArguments(@"C:\x\t.xml"));
        Assert.Contains("/ST 10:00", weekly);
        Assert.Contains(@"\""C:\Tools\Evict\Evict.exe\"" --scheduled-scan", weekly);
        Assert.Contains($"/TN \"{ScheduledScanTask.TaskName}\"", weekly);
        Assert.Contains("/F", weekly);

        var daily = ScheduledScanTask.BuildCreateArguments(@"C:\Tools\Evict\Evict.exe", "Daily", 23, 6);
        Assert.Contains("/SC DAILY", daily);
        Assert.DoesNotContain("/D ", daily);
        Assert.Contains("/ST 23:00", daily);
    }

    [Fact]
    public void ScheduledScan_ClampsAndValidates()
    {
        Assert.Contains("/ST 23:00", ScheduledScanTask.BuildCreateArguments("e.exe", "Daily", 99, 0));
        Assert.Contains("/D SAT", ScheduledScanTask.BuildCreateArguments("e.exe", "Weekly", 9, 42));
        Assert.Throws<ArgumentException>(() => ScheduledScanTask.BuildCreateArguments("e.exe", "Off", 9, 0));
        Assert.Throws<ArgumentException>(() => ScheduledScanTask.BuildCreateArguments("e.exe", "Monthly", 9, 0));
        Assert.True(ScheduledScanTask.IsValidMode("weekly"));
        Assert.False(ScheduledScanTask.IsValidMode(null));
        Assert.Equal("Off", ScheduledScanTask.Describe("Off", 9, 0));
        Assert.StartsWith("Daily at", ScheduledScanTask.Describe("Daily", 9, 0));
        Assert.StartsWith("Weekly on", ScheduledScanTask.Describe("Weekly", 9, 0));
        Assert.Contains(ScheduledScanTask.TaskName, ScheduledScanTask.BuildDeleteArguments());
        Assert.Contains("/Query", ScheduledScanTask.BuildQueryArguments());
    }

    // ───────────── command line & settings ─────────────

    [Fact]
    public void CommandLine_ParsesTrayAndScheduledScan()
    {
        var tray = CommandLineOptions.Parse(new[] { "--tray" });
        Assert.True(tray.Tray); Assert.True(tray.Headless); Assert.False(tray.IsEmpty);
        var sched = CommandLineOptions.Parse(new[] { "--scheduled-scan" });
        Assert.True(sched.ScheduledScan); Assert.True(sched.Headless); Assert.False(sched.Scan);
        Assert.False(CommandLineOptions.Parse(new[] { "--scan" }).Headless);
        Assert.True(CommandLineOptions.Parse(Array.Empty<string>()).IsEmpty);
    }

    [Fact]
    public void Settings_MigrationRaisesOldDefaultTextSizeOnce()
    {
        var old = new AppSettings { SettingsVersion = 0, UiScale = 1.0 };
        SettingsStore.Migrate(old);
        Assert.Equal(1.2, old.UiScale, 3);
        Assert.Equal(AppSettings.CurrentVersion, old.SettingsVersion);

        var custom = new AppSettings { SettingsVersion = 0, UiScale = 1.3 };
        SettingsStore.Migrate(custom);
        Assert.Equal(1.3, custom.UiScale, 3);   // a deliberate choice is kept

        var current = new AppSettings { SettingsVersion = AppSettings.CurrentVersion, UiScale = 1.0 };
        SettingsStore.Migrate(current);
        Assert.Equal(1.0, current.UiScale, 3);  // 100 % chosen after the new default → untouched
    }

    [Theory]
    [InlineData(1618, "", true)]
    [InlineData(-2147023278, "", true)]                      // 0x80070652
    [InlineData(1, "Installer failed with exit code: 0x80070652", true)]
    [InlineData(1, "Another installation is already in progress. Complete that installation before proceeding.", true)]
    [InlineData(1, "Installer hash does not match", false)]
    [InlineData(0, "", false)]
    public void Winget_DetectsBusyInstaller(int exitCode, string output, bool expected)
    {
        Assert.Equal(expected, WingetService.IsInstallerBusy(exitCode, output));
    }

    [Fact]
    public void Winget_DescribesKnownErrorCodes()
    {
        Assert.StartsWith("no applicable update", WingetService.DescribeFailure(unchecked((int)0x8A15002B), ""));
        Assert.Contains("0x8A15002B", WingetService.DescribeFailure(unchecked((int)0x8A15002B), ""));
        Assert.Contains("hash does not match", WingetService.DescribeFailure(-1978335215, "Installer hash does not match"));
        Assert.StartsWith("winget failed (0x00000063)", WingetService.DescribeFailure(99, "Something odd happened"));
        Assert.EndsWith("Something odd happened", WingetService.DescribeFailure(99, "Found Foo [Foo.Bar]\n  ██████████  10 MB / 10 MB\nSomething odd happened\n"));
        Assert.Null(WingetService.DescribeExitCode(0));
    }

    [Fact]
    public void Settings_Build4Defaults()
    {
        var s = new AppSettings();
        Assert.Equal(1.2, s.UiScale, 3);
        Assert.True(s.ShowTrayIcon);
        Assert.False(s.CloseToTray);
        Assert.False(s.StartWithWindows);
        Assert.Equal("Ask", s.InstallerDetection);
        Assert.Equal("Off", s.ScheduledScan);
        Assert.Equal(3, s.ParallelUpdates);
    }
}
