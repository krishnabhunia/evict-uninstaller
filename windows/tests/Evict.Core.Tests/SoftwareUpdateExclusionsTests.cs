using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SoftwareUpdateExclusionsTests
{
    private static UpgradablePackage Package(string id = "Example.App", string? source = "winget", string version = "2.0") =>
        new() { Id = id, Name = "Example", Source = source, AvailableVersion = version };

    [Fact]
    public void ExclusionSurvivesSettingsRoundTripAndFutureVersions()
    {
        var settings = new AppSettings { SoftwareUpdateExclusions = SoftwareUpdateExclusion.Add([], [Package()]) };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Empty(SoftwareUpdateExclusion.Visible([Package(version: "3.0")], loaded.SoftwareUpdateExclusions));
    }

    [Fact]
    public void SamePackageIdFromAnotherSourceIsStillVisible()
    {
        var exclusions = SoftwareUpdateExclusion.Add([], [Package()]);
        Assert.Single(SoftwareUpdateExclusion.Visible([Package(source: "msstore")], exclusions));
    }

    [Fact]
    public void PackageIdentityIgnoresCaseAndSurroundingWhitespace()
    {
        var exclusions = SoftwareUpdateExclusion.Add([], [Package()]);
        Assert.Empty(SoftwareUpdateExclusion.Visible([Package(" example.app ", " WINGET ")], exclusions));
        Assert.Single(SoftwareUpdateExclusion.Add(exclusions, [Package("EXAMPLE.APP", "WINGET")]));
    }

    [Fact]
    public void MissingAndEmptySourcesRepresentTheSameIdentity()
    {
        var exclusions = SoftwareUpdateExclusion.Add([], [Package(source: null)]);
        Assert.Empty(SoftwareUpdateExclusion.Visible([Package(source: "")], exclusions));
        Assert.Single(SoftwareUpdateExclusion.Visible([Package(source: "winget")], exclusions));
    }

    [Fact]
    public void RestoringOnePackageKeepsTheOtherExcluded()
    {
        var packages = new[] { Package(), Package("Other.App") };
        var exclusions = SoftwareUpdateExclusion.Add([], packages);
        exclusions.RemoveAll(e => e.Matches(packages[0]));
        Assert.Equal("Example.App", Assert.Single(SoftwareUpdateExclusion.Visible(packages, exclusions)).Id);
    }

    [Fact]
    public void RestoringAllReturnsOnlyCurrentlyAvailablePackages()
    {
        var current = new[] { Package() };
        var exclusions = SoftwareUpdateExclusion.Add([], [Package(), Package("Removed.App")]);
        exclusions.Clear();
        Assert.Same(current[0], Assert.Single(SoftwareUpdateExclusion.Visible(current, exclusions)));
    }

    [Fact]
    public void OlderSettingsStartWithoutExclusions()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"ParallelUpdates\":2}")!;
        Assert.Empty(settings.SoftwareUpdateExclusions);
        Assert.Equal(2, settings.ParallelUpdates);
    }
}
