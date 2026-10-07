using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class WingetReliabilityTests
{
    private static readonly UpgradablePackage UnknownPackage = new()
    {
        Name = "Fixture", Id = "Fixture.Package", InstalledVersion = "Unknown", Source = "winget",
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryAndExecutionCarryTheSameUnknownVersionPolicy(bool includeUnknown)
    {
        var commands = new List<string>();
        var service = new WingetService(() => "fake-winget.exe", (_, arguments, _, _, _) =>
        {
            commands.Add(arguments);
            return Task.FromResult(new ProcessResult { ExitCode = 0, StdOut = "No installed package found matching input criteria." });
        });

        await service.GetUpgradesAsync(includeUnknown, CancellationToken.None);
        await service.UpgradeAsync(UnknownPackage, CancellationToken.None, includeUnknown: includeUnknown);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => Assert.Equal(includeUnknown, command.Contains("--include-unknown", StringComparison.Ordinal)));
        Assert.Contains("--id \"Fixture.Package\" --exact", commands[1]);
    }

    [Fact]
    public async Task UnrecognizedSuccessfulOutputIsAnErrorInsteadOfAnEmptyHealthyResult()
    {
        var service = FakeResult(new ProcessResult { ExitCode = 0, StdOut = "Something changed in the command output" });

        var (packages, error) = await service.GetUpgradesAsync(false, CancellationToken.None);

        Assert.Empty(packages);
        Assert.Contains("unrecognized", error);
    }

    [Fact]
    public async Task NetworkFailureRemainsAFailedCheck()
    {
        var service = FakeResult(new ProcessResult { ExitCode = unchecked((int)0x80072EE7), StdErr = "Could not resolve the source" });

        var (packages, error) = await service.GetUpgradesAsync(false, CancellationToken.None);

        Assert.Empty(packages);
        Assert.Contains("network error", error);
    }

    [Theory]
    [InlineData(0x8A150014)]
    [InlineData(0x8A15002B)]
    public async Task NoMatchingUpgradeExitCodeWorksWithoutEnglishText(uint exitCode)
    {
        var service = FakeResult(new ProcessResult { ExitCode = unchecked((int)exitCode), StdOut = "No localized application matches." });

        var (packages, error) = await service.GetUpgradesAsync(false, CancellationToken.None);

        Assert.Empty(packages);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Name", "Version", "Available", "Source")]
    [InlineData("Nombre", "Versi\u00f3n", "Disponible", "Origen")]
    [InlineData("\u540d\u79f0", "\u7248\u672c", "\u53ef\u7528", "\u6e90")]
    public void LocalizedHeadersAndWideNamesPreserveTheExactPackageIdentity(string name, string version, string available, string source)
    {
        // The header uses double-width Chinese labels. Its actual terminal column offsets are 20,40,52,64.
        var header = Pad(name, 20) + Pad("Id", 20) + Pad(version, 12) + Pad(available, 12) + source;
        var row = Pad("\u5fae\u8f6f App", 20) + Pad("Fixture.Package", 20) + Pad("1.0", 12) + Pad("2.0", 12) + "winget";
        var parsed = WingetService.ParseUpgradeOutput(header + "\r\n" + new string('-', 80) + "\r\n" + row + "\r\n1 upgrade available.\r\n");

        Assert.True(parsed.Recognized);
        Assert.Null(parsed.Error);
        var package = Assert.Single(parsed.Packages);
        Assert.Equal("\u5fae\u8f6f App", package.Name);
        Assert.Equal("Fixture.Package", package.Id);
        Assert.Equal("1.0", package.InstalledVersion);
        Assert.Equal("2.0", package.AvailableVersion);
        Assert.Equal("winget", package.Source);
    }

    [Fact]
    public void NumericApplicationNamesAreNotMistakenForTheUpgradeCount()
    {
        var table = Pad("Name", 20) + Pad("Id", 20) + Pad("Version", 12) + Pad("Available", 12) + "Source\n"
            + new string('-', 80) + "\n"
            + Pad("7 Zip", 20) + Pad("Fixture.Package", 20) + Pad("1.0", 12) + Pad("2.0", 12) + "winget\n"
            + "1 upgrade available.\n";

        var parsed = WingetService.ParseUpgradeOutput(table);

        Assert.Null(parsed.Error);
        Assert.Equal("Fixture.Package", Assert.Single(parsed.Packages).Id);
    }

    [Fact]
    public void TruncatedIdentifierCannotBecomeAnExactUpgradeTarget()
    {
        var table = Pad("Name", 20) + Pad("Id", 20) + Pad("Version", 12) + Pad("Available", 12) + "Source\n"
            + new string('-', 80) + "\n"
            + Pad("Fixture", 20) + Pad("Fixture.Long\u2026", 20) + Pad("1.0", 12) + Pad("2.0", 12) + "winget\n";

        var parsed = WingetService.ParseUpgradeOutput(table);

        Assert.Empty(parsed.Packages);
        Assert.Contains("truncated", parsed.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("broken short row")]
    public async Task HeaderWithoutReadableRowsCannotReportAHealthyCheck(string body)
    {
        var table = Pad("Name", 20) + Pad("Id", 20) + Pad("Version", 12) + Pad("Available", 12) + "Source\n"
            + new string('-', 80) + "\n" + body + "\n";
        var service = FakeResult(new ProcessResult { ExitCode = 0, StdOut = table });

        var (packages, error) = await service.GetUpgradesAsync(false, CancellationToken.None);

        Assert.Empty(packages);
        Assert.NotNull(error);
    }

    private static string Pad(string text, int width)
    {
        int displayWidth = text.Sum(c => c >= '\u2e80' && c <= '\ua4cf' ? 2 : 1);
        return text + new string(' ', width - displayWidth);
    }

    private static WingetService FakeResult(ProcessResult result) => new(() => "fake-winget.exe", (_, _, _, _, _) => Task.FromResult(result));
}
