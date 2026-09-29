using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class RegistryCleanerTests
{
    private const string Win = @"C:\Windows";
    private static readonly HashSet<string> Disk = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\Present\app.exe",
        @"C:\Windows\SysWOW64\legacy.dll",
        @"C:\Windows\System32\rundll32.exe",
        @"C:\Tools",
    };
    private static bool Exists(string p) => Disk.Contains(p);
    private static bool FixedDrives(string root) => root == @"C:\";

    private static bool Missing(string? p) => RegistryCleanerRules.IsDefinitelyMissing(p, Exists, FixedDrives, Win);

    [Theory]
    [InlineData(@"""C:\Program Files\Gone\gone.exe"" --minimized", @"C:\Program Files\Gone\gone.exe")]
    [InlineData(@"C:\Program Files\Gone\gone.exe /background", @"C:\Program Files\Gone\gone.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe ""C:\Program Files\Gone\helper.dll"",Start", @"C:\Program Files\Gone\helper.dll")]
    [InlineData(@"rundll32.exe C:\Gone\helper.dll,Start", @"C:\Gone\helper.dll")]
    [InlineData(@"""C:\Program Files\Gone\icon.exe"",0", @"C:\Program Files\Gone\icon.exe")]
    public void MainTarget_FindsTheLaunchedFile(string data, string expected)
    {
        Assert.Equal(expected, RegistryCleanerRules.MainTarget(data), ignoreCase: true);
    }

    [Theory]
    [InlineData(@"MsiExec.exe /X{12345678-1234-1234-1234-123456789012}")]
    [InlineData(@"C:\Windows\System32\msiexec.exe /i something")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c echo hi")]      // host without a file argument
    [InlineData(@"notepad.exe")]                                   // bare name – cannot judge
    [InlineData(@"\\server\share\app.exe")]                        // network path
    [InlineData("")]
    [InlineData(null)]
    public void MainTarget_IsNullWhenNothingCanBeJudged(string? data)
    {
        Assert.Null(RegistryCleanerRules.MainTarget(data));
    }

    [Fact]
    public void Missing_OnlyForProvablyAbsentLocalFiles()
    {
        Assert.True(Missing(@"C:\Program Files\Gone\gone.exe"));
        Assert.True(Missing(@"""C:\Program Files\Gone\gone.exe"""));
        Assert.False(Missing(@"C:\Program Files\Present\app.exe"));
        Assert.False(Missing(@"C:\Tools\"));                          // folders count, trailing slash ignored
        Assert.False(Missing(@"E:\Games\game.exe"));                  // removable / absent drive
        Assert.False(Missing(@"\\nas\apps\app.exe"));                 // UNC
        Assert.False(Missing(@"%ProgramFiles%\Gone\gone.exe"));       // unexpanded variable
        Assert.False(Missing(@"relative\path.exe"));
        Assert.False(Missing(null));
    }

    [Fact]
    public void Missing_ChecksTheWow64Twin()
    {
        // A 32-bit entry says System32 but the file lives in SysWOW64.
        Assert.False(Missing(@"C:\Windows\System32\legacy.dll"));
        Assert.True(Missing(@"C:\Windows\System32\reallygone.dll"));
        Assert.Contains(@"C:\Windows\SysWOW64\x.dll", RegistryCleanerRules.SystemFolderTwins(@"C:\Windows\System32\x.dll", Win));
        Assert.Contains(@"C:\Windows\System32\x.dll", RegistryCleanerRules.SystemFolderTwins(@"C:\Windows\Sysnative\x.dll", Win));
        Assert.Empty(RegistryCleanerRules.SystemFolderTwins(@"C:\Program Files\x.dll", Win));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Foo\foo.exe.FriendlyAppName", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe.ApplicationCompany", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe", @"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\Tools\my.app.v2\run.exe.FriendlyAppName", @"C:\Tools\my.app.v2\run.exe")]
    [InlineData(@"@C:\Windows\system32\shell32.dll,-22019", null)]
    [InlineData(@"LangID", null)]
    [InlineData(@"C:\Program Files\Foo\foo.executable", null)]
    public void MuiCachePath_ParsesValueNames(string name, string? expected)
    {
        Assert.Equal(expected, RegistryCleanerRules.MuiCachePath(name));
    }

    [Fact]
    public void InstallPathOf_PrefersInstallDirAndTakesFirstPathOfLists()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"C:\Other;C:\More",
            ["installdir"] = @"""C:\Program Files\Foo\""",
            ["Version"] = "1.0",
        };
        Assert.Equal(@"C:\Program Files\Foo\", RegistryCleanerRules.InstallPathOf(values));
        Assert.Equal(@"C:\Other", RegistryCleanerRules.InstallPathOf(new Dictionary<string, string?> { ["Path"] = @"C:\Other;C:\More" }));
        Assert.Null(RegistryCleanerRules.InstallPathOf(new Dictionary<string, string?> { ["InstallDir"] = "relative" }));
    }

    [Theory]
    [InlineData("Microsoft", true)]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("Windows NT", true)]
    [InlineData("Classes", true)]
    [InlineData("WOW6432Node", true)]
    [InlineData("Policies", true)]
    [InlineData("Evict", true)]
    [InlineData("", true)]
    [InlineData("VideoLAN", false)]
    [InlineData("Notepad++", false)]
    public void ProtectedSoftwareKeys(string name, bool expected)
    {
        Assert.Equal(expected, RegistryCleanerRules.IsProtectedSoftwareKey(name));
    }

    [Fact]
    public void Rot13_DecodesUserAssistNames()
    {
        Assert.Equal(@"P:\Cebtenz Svyrf\Rivpg\Rivpg.rkr", RegistryCleanerRules.Rot13(@"C:\Program Files\Evict\Evict.exe"));
        Assert.Equal(@"C:\Program Files\Evict\Evict.exe", RegistryCleanerRules.Rot13(RegistryCleanerRules.Rot13(@"C:\Program Files\Evict\Evict.exe")));
    }
}

public class SelfCleanupTests
{
    [Theory]
    [InlineData("integration", Evict.Core.Services.SelfCleanupParts.None)]
    [InlineData("", Evict.Core.Services.SelfCleanupParts.None)]
    [InlineData("settings", Evict.Core.Services.SelfCleanupParts.Settings)]
    [InlineData("settings,history", Evict.Core.Services.SelfCleanupParts.Settings | Evict.Core.Services.SelfCleanupParts.History)]
    [InlineData("SETTINGS; traces +browser", Evict.Core.Services.SelfCleanupParts.Settings | Evict.Core.Services.SelfCleanupParts.WindowsTraces | Evict.Core.Services.SelfCleanupParts.BrowserBackups)]
    [InlineData("all", Evict.Core.Services.SelfCleanupParts.All)]
    [InlineData("installercache,bogus", Evict.Core.Services.SelfCleanupParts.InstallerCacheBackup)]
    public void ParseParts(string text, Evict.Core.Services.SelfCleanupParts expected)
    {
        Assert.Equal(expected, Evict.Core.Services.SelfCleanupService.ParseParts(text));
    }

    [Fact]
    public void FormatParts_RoundTrips()
    {
        foreach (var p in new[] { Evict.Core.Services.SelfCleanupParts.None, Evict.Core.Services.SelfCleanupParts.All, Evict.Core.Services.SelfCleanupService.DefaultParts,
                     Evict.Core.Services.SelfCleanupParts.History | Evict.Core.Services.SelfCleanupParts.InstallerCacheBackup })
            Assert.Equal(p, Evict.Core.Services.SelfCleanupService.ParseParts(Evict.Core.Services.SelfCleanupService.FormatParts(p)));
    }

    [Fact]
    public void Defaults_KeepUndoMaterialAndInstallerPackages()
    {
        var d = Evict.Core.Services.SelfCleanupService.DefaultParts;
        Assert.False(d.HasFlag(Evict.Core.Services.SelfCleanupParts.History));
        Assert.False(d.HasFlag(Evict.Core.Services.SelfCleanupParts.InstallerCacheBackup));
        Assert.True(d.HasFlag(Evict.Core.Services.SelfCleanupParts.Settings));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Evict\Evict.exe", true)]
    [InlineData(@"C:\Tools\evict.EXE", true)]
    [InlineData(@"C:\Tools\Evict.old.exe", true)]
    [InlineData(@"C:\Tools\Evict.old.4242.exe", true)]
    [InlineData(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Evict\Evict.exe", true)]
    [InlineData(@"C:\Tools\EvictHelper.exe", false)]
    [InlineData(@"C:\Tools\Evict.exe.config", false)]
    [InlineData(null, false)]
    public void IsEvictExe(string? path, bool expected)
    {
        Assert.Equal(expected, Evict.Core.Services.SelfCleanupService.IsEvictExe(path));
    }

    [Theory]
    [InlineData("history.json", Evict.Core.Services.SelfCleanupParts.History)]
    [InlineData("install-logs", Evict.Core.Services.SelfCleanupParts.History)]
    [InlineData("Registry-Backups", Evict.Core.Services.SelfCleanupParts.History)]
    [InlineData("settings.json", Evict.Core.Services.SelfCleanupParts.Settings)]
    [InlineData("evict.log", Evict.Core.Services.SelfCleanupParts.Settings)]
    [InlineData("updates", Evict.Core.Services.SelfCleanupParts.Settings)]
    public void ClassifyDataEntry(string name, Evict.Core.Services.SelfCleanupParts expected)
    {
        Assert.Equal(expected, Evict.Core.Services.SelfCleanupService.ClassifyDataEntry(name));
    }
}

public class SelfCleanupCommandLineTests
{
    [Fact]
    public void SelfCleanup_AskWithWaitPid()
    {
        var o = CommandLineOptions.Parse(new[] { "--self-cleanup", "--wait-pid", "4242", "--portable" });
        Assert.True(o.SelfCleanupAsk);
        Assert.Equal(4242, o.WaitPid);
        Assert.True(o.Portable);
        Assert.False(o.IsEmpty);
    }

    [Fact]
    public void SelfCleanup_WithParts()
    {
        var o = CommandLineOptions.Parse(new[] { "--self-cleanup", "integration" });
        Assert.Equal("integration", o.SelfCleanup);
        Assert.False(o.SelfCleanupAsk);
        Assert.Null(o.WaitPid);
    }

    [Fact]
    public void SelfCleanup_AskExplicit_AndAbsent()
    {
        Assert.True(CommandLineOptions.Parse(new[] { "/self-cleanup", "ask" }).SelfCleanupAsk);
        Assert.True(CommandLineOptions.Parse(new[] { "--self-cleanup" }).SelfCleanupAsk);
        Assert.Null(CommandLineOptions.Parse(new[] { "--scan" }).SelfCleanup);
        Assert.Null(CommandLineOptions.Parse(new[] { "--wait-pid", "abc" }).WaitPid);
    }
}
