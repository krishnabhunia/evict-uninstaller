using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class Build2Tests
{
    // ───────────── command line ─────────────

    [Fact]
    public void CommandLine_ParsesUninstallFile()
    {
        var o = CommandLineOptions.Parse(new[] { "--uninstall-file", @"C:\Program Files\Foo\foo.exe" });
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", o.UninstallFile);
        Assert.False(o.IsEmpty);
    }

    [Fact]
    public void CommandLine_BarePathIsUninstallFile()
    {
        var o = CommandLineOptions.Parse(new[] { @"C:\Users\K\Desktop\Foo.lnk" });
        Assert.Equal(@"C:\Users\K\Desktop\Foo.lnk", o.UninstallFile);
    }

    [Fact]
    public void CommandLine_FlagsAndPage()
    {
        var o = CommandLineOptions.Parse(new[] { "/scan", "--widget", "--page", "Tools", "--uninstall", "Notepad++" });
        Assert.True(o.Scan);
        Assert.True(o.Widget);
        Assert.Equal("tools", o.Page);
        Assert.Equal("Notepad++", o.UninstallName);
    }

    [Fact]
    public void CommandLine_EmptyAndUnknown()
    {
        Assert.True(CommandLineOptions.Parse(Array.Empty<string>()).IsEmpty);
        var o = CommandLineOptions.Parse(new[] { "--bogus" });
        Assert.True(o.IsEmpty);
        Assert.Single(o.Unknown);
    }

    [Fact]
    public void CommandLine_PackUnpackRoundTrip()
    {
        var args = new[] { "--uninstall-file", @"C:\a b\c.exe" };
        Assert.Equal(args, CommandLineOptions.Unpack(CommandLineOptions.Pack(args)));
    }

    // ───────────── program matcher ─────────────

    private static InstalledProgram P(string name, string? loc = null, string? exe = null, string? icon = null) => new()
    {
        Id = name, KeyName = name, Scope = RegistryScope.Machine64, RegistryPath = "", DisplayName = name, InstallLocation = loc, PrimaryExecutable = exe, DisplayIcon = icon,
    };

    [Fact]
    public void Matcher_PrefersExactExecutable_ThenDeepestFolder()
    {
        var list = new[]
        {
            P("Suite", @"C:\Program Files\Vendor"),
            P("Tool", @"C:\Program Files\Vendor\Tool", @"C:\Program Files\Vendor\Tool\tool.exe"),
        };
        Assert.Equal("Tool", ProgramMatcher.FindByPath(list, @"C:\Program Files\Vendor\Tool\tool.exe")!.DisplayName);
        Assert.Equal("Tool", ProgramMatcher.FindByPath(list, @"C:\Program Files\Vendor\Tool\bin\helper.exe")!.DisplayName);
        Assert.Equal("Suite", ProgramMatcher.FindByPath(list, @"C:\Program Files\Vendor\other.exe")!.DisplayName);
        Assert.Null(ProgramMatcher.FindByPath(list, @"D:\Elsewhere\x.exe"));
    }

    [Fact]
    public void Matcher_FallsBackToIconExeName()
    {
        var list = new[] { P("Foo Player", null, null, @"D:\old\fooplayer.exe,0") };
        Assert.Equal("Foo Player", ProgramMatcher.FindByPath(list, @"C:\somewhere\FooPlayer.exe")!.DisplayName);
    }

    [Fact]
    public void Matcher_ByName()
    {
        var list = new[] { P("Google Chrome"), P("Notepad++ (64-bit x64)"), P("Zoom Workplace") };
        Assert.Equal("Notepad++ (64-bit x64)", ProgramMatcher.FindByName(list, "notepad++")!.DisplayName);
        Assert.Equal("Google Chrome", ProgramMatcher.FindByName(list, "chrome")!.DisplayName);
        Assert.Null(ProgramMatcher.FindByName(list, "o")); // ambiguous
    }

    // ───────────── bundleware DB ─────────────

    [Theory]
    [InlineData("McAfee WebAdvisor", true)]
    [InlineData("Web Companion", true)]
    [InlineData("Google Chrome", false)]
    [InlineData("WinZip Driver Updater", true)]
    [InlineData("", false)]
    public void KnownBundleware_Matches(string name, bool expected) => Assert.Equal(expected, KnownBundleware.Match(name) != null);

    [Fact]
    public void KnownBundleware_Apply_FlagsProgram()
    {
        var p = P("ByteFence Anti-Malware");
        KnownBundleware.Apply(new[] { p });
        Assert.True(p.IsKnownBundleware);
        Assert.True(p.IsBundleSuspect);
        Assert.Contains("known-bundleware", p.BundleGroupNote);
    }

    // ───────────── startup approved flags ─────────────

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    public void Startup_ApprovedFlag(byte b, bool enabled) => Assert.Equal(enabled, StartupService.IsEnabledFlag(b));

    [Fact]
    public void Startup_BuildApprovedValue()
    {
        var on = StartupService.BuildApprovedValue(true);
        Assert.Equal(12, on.Length);
        Assert.Equal(0x02, on[0]);
        Assert.All(on.Skip(1), x => Assert.Equal(0, x));
        var off = StartupService.BuildApprovedValue(false);
        Assert.Equal(0x03, off[0]);
        Assert.True(BitConverter.ToInt64(off, 4) > 0);
    }

    // ───────────── residual scanner helpers ─────────────

    [Fact]
    public void Residual_InstalledKeySet_ContainsNamesPublishersAndLeaves()
    {
        var p = P("Google Chrome", @"C:\Program Files\Google\Chrome\Application", @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        p.Publisher = "Google LLC";
        var set = ResidualScanner.BuildInstalledKeySet(new[] { p });
        Assert.Contains("googlechrome", set);
        Assert.Contains("google", set);
        Assert.Contains("chrome", set);
        Assert.Contains("application", set);
    }

    [Fact]
    public void Residual_FingerprintFromHistory_UsesNameAndLocation()
    {
        var h = new UninstallHistoryEntry { ProgramName = "Foo Media Player", Publisher = "Foo Labs Inc.", InstallLocation = @"C:\Program Files\FooPlayer" };
        var fp = ResidualScanner.FingerprintFromHistory(h);
        Assert.Contains(fp.NameKeys, k => k.Key == "foomediaplayer" && k.Confidence == LeftoverConfidence.High);
        Assert.Contains(fp.NameKeys, k => k.Key == "fooplayer" && k.Confidence == LeftoverConfidence.Medium);
        Assert.DoesNotContain(fp.NameKeys, k => k.Confidence == LeftoverConfidence.Low);
        Assert.Equal("foolabs", fp.PublisherToken);
    }
}
