using System.Text;
using Evict.Core.Util;
using Microsoft.Win32;
using Xunit;

namespace Evict.Core.Tests;

public class RegistryBackupTests
{
    [Fact]
    public void String_IsQuotedAndEscaped()
    {
        Assert.Equal(@"""Path""=""C:\\Program Files\\Foo \""x\""""", RegFileFormat.FormatValue(RegFileFormat.String("Path", @"C:\Program Files\Foo ""x""")));
    }

    [Fact]
    public void DefaultValue_UsesAt()
    {
        Assert.Equal(@"@=""hello""", RegFileFormat.FormatValue(RegFileFormat.String("", "hello")));
    }

    [Fact]
    public void StringWithLineBreak_FallsBackToHex1()
    {
        var line = RegFileFormat.FormatValue(RegFileFormat.String("x", "a\nb"));
        Assert.StartsWith("\"x\"=hex(1):61,00,0a,00,62,00", line);
    }

    [Fact]
    public void Dword_IsEightHexDigits()
    {
        Assert.Equal("\"Enabled\"=dword:0000002a", RegFileFormat.FormatValue(RegFileFormat.Dword("Enabled", 42)));
        Assert.Equal("\"Max\"=dword:ffffffff", RegFileFormat.FormatValue(RegFileFormat.Dword("Max", uint.MaxValue)));
    }

    [Fact]
    public void Qword_ExpandSz_MultiSz_Binary_UseHexForms()
    {
        Assert.Equal("\"Q\"=hex(b):01,00,00,00,00,00,00,00", RegFileFormat.FormatValue(RegFileFormat.Qword("Q", 1)));
        Assert.Equal("\"E\"=hex(2):25,00,54,00,00,00", RegFileFormat.FormatValue(RegFileFormat.ExpandString("E", "%T")));
        Assert.Equal("\"M\"=hex(7):61,00,00,00,62,00,00,00,00,00", RegFileFormat.FormatValue(RegFileFormat.MultiString("M", new[] { "a", "b" })));
        Assert.Equal("\"B\"=hex:de,ad", RegFileFormat.FormatValue(new RegRawValue("B", RegFileFormat.RegBinary, new byte[] { 0xde, 0xad })));
        Assert.Equal("\"N\"=hex(0):", RegFileFormat.FormatValue(new RegRawValue("N", RegFileFormat.RegNone, Array.Empty<byte>())));
    }

    [Fact]
    public void DwordWithWrongLength_IsWrittenAsHex4()
    {
        Assert.Equal("\"D\"=hex(4):01,02", RegFileFormat.FormatValue(new RegRawValue("D", RegFileFormat.RegDword, new byte[] { 1, 2 })));
    }

    [Theory]
    [InlineData(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Foo", @"HKEY_LOCAL_MACHINE\SOFTWARE\Foo")]
    [InlineData(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Foo", @"HKEY_LOCAL_MACHINE\SOFTWARE\Foo")]
    [InlineData(RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\WOW6432Node\Foo", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Foo")]
    [InlineData(RegistryHive.LocalMachine, RegistryView.Registry32, @"SYSTEM\X", @"HKEY_LOCAL_MACHINE\SYSTEM\X")]
    [InlineData(RegistryHive.CurrentUser, RegistryView.Registry32, @"\Software\Foo\", @"HKEY_CURRENT_USER\Software\Foo")]
    public void KeyPath_PreservesLogicalLocation(RegistryHive hive, RegistryView view, string sub, string expected)
    {
        Assert.Equal(expected, RegFileFormat.KeyPath(hive, view, sub));
    }

    [Fact]
    public void Mixed_view_backups_preserve_shared_key_paths_and_restore_each_view_explicitly()
    {
        const string applications = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Applications\fixture.exe";
        const string appPaths = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\fixture.exe";
        var document = RegFileFormat.BuildDocument(new[]
        {
            new RegKeyBlock(applications, new[] { RegFileFormat.String("", "32-bit fixture") }) { View = RegistryView.Registry32 },
            new RegKeyBlock(appPaths, Array.Empty<RegRawValue>()) { View = RegistryView.Registry32 },
            new RegKeyBlock(applications, new[] { RegFileFormat.String("", "64-bit fixture") }) { View = RegistryView.Registry64 },
        });
        Assert.Contains(RegFileFormat.RestoreHint, document);
        Assert.DoesNotContain("WOW6432Node", document);
        var parts = RegFileFormat.RestoreDocuments(document);
        Assert.Equal(2, parts.Count);
        var x86 = Assert.Single(parts, p => p.View == RegistryView.Registry32);
        var x64 = Assert.Single(parts, p => p.View == RegistryView.Registry64);
        Assert.Contains(applications, x86.Document);
        Assert.Contains(appPaths, x86.Document);
        Assert.Contains("32-bit fixture", x86.Document);
        Assert.DoesNotContain("64-bit fixture", x86.Document);
        Assert.Contains("64-bit fixture", x64.Document);
        Assert.EndsWith(" /reg:32", Evict.Core.Services.RegistryBackupService.ImportArguments("fixture.reg", x86.View));
        Assert.EndsWith(" /reg:64", Evict.Core.Services.RegistryBackupService.ImportArguments("fixture.reg", x64.View));
    }

    [Fact]
    public void Legacy_backup_keeps_default_view_and_malformed_view_metadata_is_rejected()
    {
        var legacy = RegFileFormat.BuildDocument(new[] { new RegKeyBlock(@"HKEY_CURRENT_USER\Software\Fixture", Array.Empty<RegRawValue>()) });
        var part = Assert.Single(RegFileFormat.RestoreDocuments(legacy));
        Assert.Equal(RegistryView.Default, part.View);
        Assert.Equal(legacy, part.Document);
        Assert.Equal("import \"fixture.reg\"", Evict.Core.Services.RegistryBackupService.ImportArguments("fixture.reg", part.View));
        Assert.Throws<FormatException>(() => RegFileFormat.RestoreDocuments(RegFileFormat.Header + "\r\n" + RegFileFormat.ViewMarker + "invalid\r\n[HKEY_CURRENT_USER\\Software\\Fixture]"));
    }

    [Fact]
    public void Document_HasHeaderBlocksAndBlankLines()
    {
        var doc = RegFileFormat.BuildDocument(new[]
        {
            new RegKeyBlock(@"HKEY_CURRENT_USER\Software\Foo", new[] { RegFileFormat.String("", "x"), RegFileFormat.Dword("n", 1) }),
            new RegKeyBlock(@"HKEY_CURRENT_USER\Software\Foo\Sub", Array.Empty<RegRawValue>()),
        });
        Assert.Equal(
            "Windows Registry Editor Version 5.00\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Foo]\r\n@=\"x\"\r\n\"n\"=dword:00000001\r\n\r\n" +
            "[HKEY_CURRENT_USER\\Software\\Foo\\Sub]\r\n\r\n", doc);
    }

    [Fact]
    public void Encode_IsUtf16LeWithBom()
    {
        var bytes = RegFileFormat.Encode("W");
        Assert.Equal(new byte[] { 0xFF, 0xFE, (byte)'W', 0 }, bytes);
    }

    [Fact]
    public void BackupFileName_RoundTrips_AndIsSafe()
    {
        var when = new DateTime(2026, 9, 29, 10, 15, 30);
        var name = RegFileFormat.BackupFileName(when, "Uninstall Foo: Bar/Baz?");
        Assert.Equal("2026-09-29_101530 Uninstall Foo_ Bar_Baz_.reg", name);
        var parsed = RegFileFormat.ParseBackupFileName(name);
        Assert.NotNull(parsed);
        Assert.Equal(when, parsed!.Value.When);
        Assert.Equal("Uninstall Foo_ Bar_Baz_", parsed.Value.Label);
        Assert.Null(RegFileFormat.ParseBackupFileName("random.reg"));
        Assert.Null(RegFileFormat.ParseBackupFileName("2026-09-29_101530.reg"));
        Assert.EndsWith(" backup.reg", RegFileFormat.BackupFileName(when, "  ...  "));
    }
}
