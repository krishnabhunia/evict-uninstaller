using Evict.Core.Models;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class UninstallCommandParserTests
{
    [Fact]
    public void Parse_QuotedPathWithArgs()
    {
        var c = UninstallCommandParser.Parse("\"C:\\Program Files\\Foo\\unins000.exe\" /SILENT");
        Assert.NotNull(c);
        Assert.Equal(@"C:\Program Files\Foo\unins000.exe", c!.FileName);
        Assert.Equal("/SILENT", c.Arguments);
    }

    [Fact]
    public void Parse_UnquotedPathWithSpaces()
    {
        var c = UninstallCommandParser.Parse(@"C:\Program Files (x86)\Foo Bar\uninstall.exe /S");
        Assert.Equal(@"C:\Program Files (x86)\Foo Bar\uninstall.exe", c!.FileName);
        Assert.Equal("/S", c.Arguments);
    }

    [Fact]
    public void Parse_UnquotedPathNoArgs()
    {
        var c = UninstallCommandParser.Parse(@"C:\Program Files\Foo\Uninstall.EXE");
        Assert.Equal(@"C:\Program Files\Foo\Uninstall.EXE", c!.FileName);
        Assert.Equal("", c.Arguments);
    }

    [Fact]
    public void Parse_MsiExecCompact()
    {
        var c = UninstallCommandParser.Parse("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
        Assert.Equal("MsiExec.exe", c!.FileName);
        Assert.Equal("/I{12345678-1234-1234-1234-123456789012}", c.Arguments);
        Assert.True(UninstallCommandParser.IsMsiExec(c));
    }

    [Fact]
    public void Parse_Rundll32()
    {
        var c = UninstallCommandParser.Parse(@"rundll32.exe C:\Foo\setup.dll,Uninstall /x");
        Assert.Equal("rundll32.exe", c!.FileName);
        Assert.Equal(@"C:\Foo\setup.dll,Uninstall /x", c.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ReturnsNull(string? raw) => Assert.Null(UninstallCommandParser.Parse(raw));

    [Fact]
    public void NormalizeMsiExec_ConvertsInstallToUninstall_AndAddsQuiet()
    {
        var c = new UninstallCommand("msiexec.exe", "/I{12345678-1234-1234-1234-123456789012}");
        var n = UninstallCommandParser.NormalizeMsiExec(c, quiet: true);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789012} /qn /norestart", n.Arguments);
        var n2 = UninstallCommandParser.NormalizeMsiExec(c, quiet: false);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789012}", n2.Arguments);
    }

    [Fact]
    public void NormalizeMsiExec_DoesNotDuplicateQuietSwitches()
    {
        var c = new UninstallCommand("msiexec.exe", "/x {12345678-1234-1234-1234-123456789012} /qn");
        var n = UninstallCommandParser.NormalizeMsiExec(c, quiet: true);
        Assert.Equal("/x {12345678-1234-1234-1234-123456789012} /qn /norestart", n.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Foo\unins000.exe", InstallerKind.InnoSetup)]
    [InlineData(@"C:\Foo\Uninstall.exe", InstallerKind.Nsis)]
    [InlineData(@"C:\Foo\uninst.exe", InstallerKind.Nsis)]
    [InlineData(@"C:\Users\x\AppData\Local\Foo\Update.exe", InstallerKind.SquirrelOrElectron)]
    [InlineData(@"C:\Windows\System32\msiexec.exe", InstallerKind.Msi)]
    [InlineData(@"C:\Foo\remove.exe", InstallerKind.Other)]
    [InlineData("", InstallerKind.Unknown)]
    public void DetectInstaller_ByName(string exe, InstallerKind expected) =>
        Assert.Equal(expected, UninstallCommandParser.DetectInstaller(exe));

    [Fact]
    public void DetectInstaller_ByContent()
    {
        byte[] Head(string _) => System.Text.Encoding.ASCII.GetBytes("garbage Nullsoft Install System garbage");
        Assert.Equal(InstallerKind.Nsis, UninstallCommandParser.DetectInstaller(@"C:\Foo\remove.exe", Head));
        byte[] Inno(string _) => System.Text.Encoding.ASCII.GetBytes("xx Inno Setup Setup Data xx");
        Assert.Equal(InstallerKind.InnoSetup, UninstallCommandParser.DetectInstaller(@"C:\Foo\remove.exe", Inno));
    }

    [Fact]
    public void Resolve_PrefersQuietStringWhenQuiet()
    {
        var p = Program(uninstall: "\"C:\\Foo\\unins000.exe\"", quiet: "\"C:\\Foo\\unins000.exe\" /VERYSILENT");
        var cmd = UninstallCommandParser.Resolve(p, quiet: true);
        Assert.Equal("/VERYSILENT", cmd!.Arguments);
        var loud = UninstallCommandParser.Resolve(p, quiet: false);
        Assert.Equal("", loud!.Arguments);
    }

    [Fact]
    public void Resolve_AddsSilentSwitchesForKnownInstallers()
    {
        var p = Program(uninstall: "\"C:\\Foo\\unins000.exe\"");
        var cmd = UninstallCommandParser.Resolve(p, quiet: true);
        Assert.Contains("/VERYSILENT", cmd!.Arguments);
        Assert.Contains("/NORESTART", cmd.Arguments);
    }

    [Fact]
    public void Resolve_MsiProduct_UsesMsiexec()
    {
        var p = Program(uninstall: "MsiExec.exe /I{12345678-1234-1234-1234-123456789012}", isMsi: true, code: "{12345678-1234-1234-1234-123456789012}");
        var cmd = UninstallCommandParser.Resolve(p, quiet: true);
        Assert.EndsWith("msiexec.exe", cmd!.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789012} /qn /norestart", cmd.Arguments);
    }

    [Fact]
    public void Resolve_NoCommand_ReturnsNull()
    {
        var p = Program(uninstall: null);
        Assert.Null(UninstallCommandParser.Resolve(p, quiet: false));
    }

    [Fact]
    public void IsGuid_Works()
    {
        Assert.True(UninstallCommandParser.IsGuid("{12345678-1234-1234-1234-123456789012}"));
        Assert.False(UninstallCommandParser.IsGuid("Notepad++"));
        Assert.False(UninstallCommandParser.IsGuid(null));
    }

    private static InstalledProgram Program(string? uninstall, string? quiet = null, bool isMsi = false, string? code = null) => new()
    {
        Id = "t", KeyName = code ?? "Foo", Scope = RegistryScope.Machine64, RegistryPath = "HKLM\\...", DisplayName = "Foo",
        UninstallString = uninstall, QuietUninstallString = quiet, IsMsi = isMsi, MsiProductCode = code,
    };
}
