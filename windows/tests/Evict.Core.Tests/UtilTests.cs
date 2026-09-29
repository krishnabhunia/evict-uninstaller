using System.Text;
using Evict.Core.Services;
using Evict.Core.Util;
using Microsoft.Win32;
using Xunit;

namespace Evict.Core.Tests;

public class UtilTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.00 KB")]
    [InlineData(1536L, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(250L * 1024 * 1024, "250 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void SizeFormatter_Formats(long? bytes, string expected) => Assert.Equal(expected, SizeFormatter.Format(bytes));

    [Theory]
    [InlineData("C:\\App\\app.exe,0", "C:\\App\\app.exe", 0)]
    [InlineData("\"C:\\App\\app.exe\",-101", "C:\\App\\app.exe", -101)]
    [InlineData("C:\\App\\app.ico", "C:\\App\\app.ico", 0)]
    [InlineData("C:\\App, Inc\\app.exe", "C:\\App, Inc\\app.exe", 0)]
    [InlineData("", "", 0)]
    public void SplitIconPath_Works(string input, string path, int index)
    {
        var (p, i) = PathUtil.SplitIconPath(input);
        Assert.Equal(path, p);
        Assert.Equal(index, i);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Foo\bar.exe", @"C:\Program Files\Foo", true)]
    [InlineData(@"C:\Program Files\Foo", @"C:\Program Files\Foo", true)]
    [InlineData(@"C:\Program Files\Foobar\x.exe", @"C:\Program Files\Foo", false)]
    [InlineData(@"c:\program files\foo\x.exe", @"C:\Program Files\Foo\", true)]
    [InlineData(@"D:\Other", @"C:\Program Files\Foo", false)]
    [InlineData("", @"C:\Foo", false)]
    public void IsUnder_Works(string path, string root, bool expected) => Assert.Equal(expected, PathUtil.IsUnder(path, root));

    [Theory]
    [InlineData(@"C:\", 0)]
    [InlineData(@"C:\Program Files", 1)]
    [InlineData(@"C:\Program Files\Foo", 2)]
    [InlineData(@"C:\Program Files\Foo\", 2)]
    [InlineData(@"C:\Users\K\AppData\Local\Foo", 5)]
    public void Depth_Works(string path, int expected) => Assert.Equal(expected, PathUtil.Depth(path));

    [Fact]
    public void Rot13_RoundTrips()
    {
        Assert.Equal("Hello", UserAssistReader.Rot13("Uryyb"));
        Assert.Equal("Uryyb", UserAssistReader.Rot13("Hello"));
        Assert.Equal("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe", UserAssistReader.Rot13("{1NP14R77-02R7-4R5Q-O744-2RO1NR5198O7}\\abgrcnq.rkr"));
    }

    [Fact]
    public void UserAssist_ParseValue()
    {
        var data = new byte[72];
        BitConverter.GetBytes(7).CopyTo(data, 4);
        var ft = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        BitConverter.GetBytes(ft).CopyTo(data, 60);
        var parsed = UserAssistReader.ParseValue(data);
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.Value.RunCount);
        Assert.Equal(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), parsed.Value.LastRun!.Value.ToUniversalTime());
        Assert.Null(UserAssistReader.ParseValue(new byte[16]));
        Assert.Null(UserAssistReader.ParseValue(null));
    }

    [Fact]
    public void ExtractJson_StripsWarnings()
    {
        Assert.Equal("[{\"a\":1}]", PowerShellRunner.ExtractJson("WARNING: something\n[{\"a\":1}]\n"));
        Assert.Equal("{\"a\":1}", PowerShellRunner.ExtractJson("{\"a\":1}"));
        Assert.Equal("", PowerShellRunner.ExtractJson("no json here"));
    }

    [Fact]
    public void RegistryPaths_Display_ShowsWow6432Node()
    {
        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\Foo", RegistryPaths.Display(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Foo"));
        Assert.Equal(@"HKLM\SOFTWARE\Foo", RegistryPaths.Display(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Foo"));
        Assert.Equal(@"HKCU\Software\Foo → ""Bar""", RegistryPaths.Display(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Foo", "Bar"));
        Assert.Equal(@"HKCU\Software\Foo → ""(Default)""", RegistryPaths.Display(RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Foo", ""));
    }

    [Fact]
    public void RegistryPaths_Split()
    {
        Assert.Equal(("SOFTWARE\\Foo", "Bar"), RegistryPaths.Split(@"SOFTWARE\Foo\Bar"));
        Assert.Equal(("", "SOFTWARE"), RegistryPaths.Split("SOFTWARE"));
    }

    [Fact]
    public void ShortcutInspector_FindsInstallPathInAnsiAndUnicode()
    {
        var ansi = Encoding.Latin1.GetBytes("LNK....C:\\Program Files\\Foo\\foo.exe\0....");
        Assert.True(ShortcutInspector.ReferencesBytes(ansi, @"C:\Program Files\Foo", Array.Empty<string>()));
        var uni = Encoding.Unicode.GetBytes("LNK....C:\\Program Files\\Foo\\foo.exe\0....");
        Assert.True(ShortcutInspector.ReferencesBytes(uni, @"C:\Program Files\Foo", Array.Empty<string>()));
        Assert.False(ShortcutInspector.ReferencesBytes(uni, @"C:\Program Files\Bar", Array.Empty<string>()));
    }

    [Fact]
    public void ShortcutInspector_ExeNames_IgnoreGeneric()
    {
        var bytes = Encoding.Latin1.GetBytes("....C:\\Somewhere\\uninstall.exe....");
        Assert.False(ShortcutInspector.ReferencesBytes(bytes, null, new[] { "uninstall.exe" }));
        var bytes2 = Encoding.Latin1.GetBytes("....C:\\Somewhere\\notepad++.exe....");
        Assert.True(ShortcutInspector.ReferencesBytes(bytes2, null, new[] { "notepad++.exe" }));
    }

    [Theory]
    [InlineData("20240115", 2024, 1, 15)]
    [InlineData("2024-01-15", 2024, 1, 15)]
    [InlineData("2024/01/15", 2024, 1, 15)]
    public void ParseInstallDate_Formats(string input, int y, int m, int d)
    {
        var dt = InstalledProgramsService.ParseInstallDate(input);
        Assert.Equal(new DateTime(y, m, d), dt!.Value.Date);
    }

    [Fact]
    public void ParseInstallDate_Garbage_ReturnsNull()
    {
        Assert.Null(InstalledProgramsService.ParseInstallDate("not a date"));
        Assert.Null(InstalledProgramsService.ParseInstallDate(null));
    }

    [Fact]
    public void InstallMonitor_SnapshotKeyRoundTrip()
    {
        var (hive, view, sub) = InstallMonitorService.ParseSnapshotKey(@"HKLM|32|SOFTWARE\Foo\Bar");
        Assert.Equal(RegistryHive.LocalMachine, hive);
        Assert.Equal(RegistryView.Registry32, view);
        Assert.Equal(@"SOFTWARE\Foo\Bar", sub);
        Assert.Equal(@"HKLM\SOFTWARE\WOW6432Node\Foo\Bar", InstallMonitorService.DisplaySnapshotKey(@"HKLM|32|SOFTWARE\Foo\Bar"));
    }
}
