using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

/// <summary>Design SP1: the start-up splash.</summary>
public sealed class StartupSplashTests
{
    [Theory]
    [InlineData(new string[0], true)]                              // normal start from the Start menu
    [InlineData(new[] { "--page", "programs" }, true)]
    [InlineData(new[] { "--uninstall-file", "C:\\x.lnk" }, true)]  // Explorer "Uninstall with Evict"
    [InlineData(new[] { "--updated" }, true)]
    [InlineData(new[] { "--wait-pid", "42" }, true)]               // the elevated copy after "Start as administrator"
    [InlineData(new[] { "--tray" }, false)]                        // sign-in start, hidden in the tray
    [InlineData(new[] { "--scheduled-scan" }, false)]
    [InlineData(new[] { "--exit" }, false)]                        // Setup asking a running Evict to close
    [InlineData(new[] { "--self-cleanup" }, false)]
    public void ShowsOnlyForStartsTheUserSees(string[] args, bool expected) =>
        Assert.Equal(expected, StartupSplashRules.ShouldShow(CommandLineOptions.Parse(args)));

    [Fact]
    public void StepsCountUpAndTheBarFills()
    {
        StartupStep[] order = [StartupStep.Settings, StartupStep.History, StartupStep.Programs, StartupStep.Window];
        Assert.Equal(["1 of 4", "2 of 4", "3 of 4", "4 of 4"], order.Select(StartupSplashRules.CountText).ToArray());
        var bars = order.Select(StartupSplashRules.Progress).ToList();
        Assert.Equal(bars.OrderBy(b => b), bars);
        Assert.All(bars, b => Assert.InRange(b, 0.0, 1.0));
        Assert.Equal("Loading your programs…", StartupSplashRules.Describe(StartupStep.Programs).Text);
    }

    [Fact]
    public void AdministratorPromptHidesTheBar()
    {
        var (text, _, showProgress) = StartupSplashRules.Describe(StartupStep.WaitingForAdministrator);
        Assert.Equal("Waiting for administrator permission…", text);
        Assert.False(showProgress);
    }

    [Fact]
    public void WaitsAreOpenNowAt8SecondsAndOpenAnywayAt20()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), StartupSplashRules.OfferOpenNowAfter);
        Assert.Equal(TimeSpan.FromSeconds(20), StartupSplashRules.OpenAnywayAfter);
        Assert.True(StartupSplashRules.OpenAnywayAfter > StartupSplashRules.OfferOpenNowAfter);
    }

    [Fact]
    public void ErrorTextIsTheFirstLineOnly()
    {
        Assert.Equal("Access to the path is denied.", StartupSplashRules.ErrorText(new IOException("Access to the path is denied.\n   at Foo.Bar()")));
        Assert.Equal("Something went wrong while starting.", StartupSplashRules.ErrorText(new Exception("  ")));
    }
}
