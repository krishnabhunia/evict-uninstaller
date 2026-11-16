using System.Text;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SoftwareUpdateSourcesTests
{
    [Theory]
    [InlineData("25.0.5.0", "25.0.5.0.3", false)]
    [InlineData("1.2.3.9", "1.2.3.10", false)]
    [InlineData("1.2.3", "1.2.4.0", true)]
    [InlineData("1.2.9", "1.3.0", true)]
    [InlineData("1.9.9", "2.0.0", true)]
    [InlineData("1.3.0", "1.2.99.999", false)]
    [InlineData("23.01", "24.09", true)]
    [InlineData("v1.2.3", "1.2.3.4", false)]
    [InlineData("1.2.3-beta.1", "1.2.4", true)]
    [InlineData("Unknown", "1.2.3", false)]
    [InlineData("1.2.3", "unrecognized", false)]
    public void UpdatesCompareOnlyThreeNumericComponents(string installed, string available, bool expected)
        => Assert.Equal(expected, SoftwareVersionPolicy.ShouldOffer(installed, available, false));

    [Fact]
    public void UnknownInstalledVersionRequiresOptInAndKnownAvailableVersion()
    {
        Assert.True(SoftwareVersionPolicy.ShouldOffer("Unknown", "v3.2.1.7", true));
        Assert.False(SoftwareVersionPolicy.ShouldOffer("Unknown", "Unknown", true));
        Assert.False(SoftwareVersionPolicy.ShouldOffer("3.2.1", "3.2.1.7", true));
    }

    [Fact]
    public void ChocolateyParsesMachineReadableOutput()
    {
        var parsed = ChocolateyService.ParseOutdated("Chocolatey v2.4.0\nfoo|1.2.3.4|1.2.4.0|false\nbar|23.0|24.0|false\n2 packages outdated.\n", out var error);
        Assert.Null(error);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("chocolatey", parsed[0].Source);
        Assert.Equal("foo", parsed[0].Id);
        Assert.Equal("1.2.3.4", parsed[0].InstalledVersion);
    }

    [Theory]
    [InlineData("evil --yes|1.0.0|2.0.0")]
    [InlineData("foo|1.0.0")]
    [InlineData("Unexpected successful prose")]
    public void ChocolateyRejectsUnrecognizedRows(string output)
    {
        Assert.Empty(ChocolateyService.ParseOutdated(output, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ScoopParsesPositionedColumnsAndSkipsHeldApps()
    {
        var header = "Name".PadRight(18) + "Installed Version".PadRight(21) + "Latest Version".PadRight(17) + "Missing Dependencies".PadRight(22) + "Info";
        var separator = "----".PadRight(18) + "-----------------".PadRight(21) + "--------------".PadRight(17) + "--------------------".PadRight(22) + "----";
        var row1 = "aws".PadRight(18) + "2.18.2".PadRight(21) + "2.24.14".PadRight(17);
        var row2 = "nodejs".PadRight(18) + "20.1.0".PadRight(21) + "20.2.0".PadRight(17) + "".PadRight(22) + "hold";
        var parsed = ScoopService.ParseStatus("Scoop is up to date.\n" + header + "\n" + separator + "\n" + row1 + "\n" + row2, out var error);
        Assert.Null(error);
        var pkg = Assert.Single(parsed);
        Assert.Equal("aws", pkg.Id);
        Assert.Equal("2.18.2", pkg.InstalledVersion);
        Assert.Equal("2.24.14", pkg.AvailableVersion);
        Assert.Equal("scoop", pkg.Source);
    }

    [Fact]
    public void ScoopDoesNotSilentlyTreatUnknownOutputAsNoUpdates()
    {
        Assert.Empty(ScoopService.ParseStatus("unexpected output", out var error));
        Assert.Contains("unrecognized", error);
    }

    [Fact]
    public void ScoopEscapesScriptPathsAndRejectsArbitraryIdentifiers()
    {
        var args = ScoopService.BuildPowerShellArguments(@"C:\User's folder\scoop.ps1", "update", "7zip");
        var command = Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ').Last()));
        Assert.Contains("C:\\User''s folder\\scoop.ps1", command);
        Assert.Contains("update '7zip'", command);
        Assert.Throws<ArgumentException>(() => ScoopService.BuildPowerShellArguments("scoop.ps1", "update", "7zip'; exit 0"));
    }

    [Fact]
    public async Task ScansInstalledManagersAndHidesBuildOnlyDifferences()
    {
        var winget = DisabledWinget();
        var chocolatey = new ChocolateyService(() => "choco.exe", (_, _, _, _, _) => Task.FromResult(new ProcessResult
        {
            ExitCode = 2, StdOut = "jdk|25.0.5.0|25.0.5.0.3|false\npython|3.12.4.0|3.12.5.1|false\n",
        }));
        var scoop = new ScoopService(() => null, (_, _, _, _, _) => throw new InvalidOperationException());
        var scan = await new SoftwareUpdateSourcesService(winget, chocolatey, scoop).GetUpgradesAsync(false, CancellationToken.None);

        Assert.Null(scan.Error);
        Assert.Null(scan.Warning);
        Assert.Equal("Chocolatey", scan.CheckedSources);
        Assert.Equal("python", Assert.Single(scan.Packages).Id);
    }

    [Fact]
    public async Task PartialSourceErrorPreservesSuccessfulResultsAndWarns()
    {
        var winget = new WingetService(() => "winget.exe", (_, _, _, _, _) => Task.FromResult(new ProcessResult
        {
            ExitCode = 0, StdOut = WingetTable("Java", "Oracle.JDK.25", "25.0.5.0", "25.0.5.0.3", "winget")
                + WingetRow("Git", "Git.Git", "2.4.0", "2.5.0", "winget") + "2 upgrades available.\n",
        }));
        var chocolatey = new ChocolateyService(() => "choco.exe", (_, _, _, _, _) => Task.FromResult(new ProcessResult { ExitCode = 1, StdErr = "Feed unreachable" }));
        var scoop = new ScoopService(() => null, (_, _, _, _, _) => throw new InvalidOperationException());
        var scan = await new SoftwareUpdateSourcesService(winget, chocolatey, scoop).GetUpgradesAsync(false, CancellationToken.None);

        Assert.Null(scan.Error);
        Assert.Contains("Chocolatey", scan.Warning);
        Assert.Equal("Git.Git", Assert.Single(scan.Packages).Id);
    }

    [Fact]
    public async Task EntirelyFailedSourcesProduceErrorNotCleanBillOfHealth()
    {
        var chocolatey = new ChocolateyService(() => "choco.exe", (_, _, _, _, _) => Task.FromResult(new ProcessResult { ExitCode = 1 }));
        var scan = await new SoftwareUpdateSourcesService(DisabledWinget(), chocolatey,
            new ScoopService(() => null, (_, _, _, _, _) => throw new InvalidOperationException()))
            .GetUpgradesAsync(false, CancellationToken.None);

        Assert.NotNull(scan.Error);
        Assert.Empty(scan.Packages);
    }

    [Theory]
    [InlineData("winget", "winget.exe", "upgrade --id")]
    [InlineData("msstore", "winget.exe", "--source \"msstore\"")]
    [InlineData("chocolatey", "choco.exe", "upgrade \"sample\"")]
    [InlineData("scoop", "powershell.exe", "-EncodedCommand")]
    public async Task UpgradeIsRoutedToOriginatingManager(string source, string expectedExe, string expectedArgument)
    {
        string? executedExe = null;
        string? executedArgs = null;
        Task<ProcessResult> Run(string exe, string args, CancellationToken _, TimeSpan __, Action<string>? ___)
        {
            executedExe = exe;
            executedArgs = args;
            return Task.FromResult(new ProcessResult { ExitCode = 0 });
        }
        var service = new SoftwareUpdateSourcesService(new WingetService(() => "winget.exe", Run),
            new ChocolateyService(() => "choco.exe", Run), new ScoopService(() => "scoop.ps1", Run));
        var pkg = new UpgradablePackage { Name = "Sample", Id = "sample", InstalledVersion = "1.0.0", AvailableVersion = "1.0.1", Source = source };

        var result = await service.UpgradeWithResultAsync(pkg, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(expectedExe, executedExe);
        Assert.Contains(expectedArgument, executedArgs);
    }

    private static WingetService DisabledWinget() => new(() => null, (_, _, _, _, _) => throw new InvalidOperationException());

    private static string WingetTable(string name, string id, string installed, string available, string source) =>
        "Name".PadRight(20) + "Id".PadRight(22) + "Version".PadRight(12) + "Available".PadRight(14) + "Source\n"
        + new string('-', 82) + "\n"
        + WingetRow(name, id, installed, available, source);

    private static string WingetRow(string name, string id, string installed, string available, string source) =>
        name.PadRight(20) + id.PadRight(22) + installed.PadRight(12) + available.PadRight(14) + source + "\n";
}
