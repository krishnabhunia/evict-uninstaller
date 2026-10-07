using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class ScannerSafetyTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Acme\..\..\Windows", @"C:\Windows")]
    [InlineData(@"C:\Apps\Foo\.\bin\..\foo.exe", @"C:\Apps\Foo\foo.exe")]
    [InlineData(@"c:/Apps/Foo/../Bar/", @"C:\Apps\Bar")]
    [InlineData(@"\\server\share\App\..\Other", @"\\server\share\Other")]
    [InlineData(@"C:\", @"C:\")]
    public void AbsolutePaths_CollapseParentSegments(string input, string expected)
    {
        Assert.True(PathUtil.TryCanonicalizeAbsolute(input, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(@"C:Apps\Foo")]
    [InlineData(@"Apps\Foo")]
    [InlineData(@"\Windows\System32")]
    [InlineData(@"\\?\C:\Apps\Foo")]
    [InlineData(@"C:\Apps\Foo:stream")]
    [InlineData(@"C:\Apps\Foo.\bar")]
    public void AmbiguousPaths_FailClosed(string path)
    {
        Assert.False(PathUtil.TryCanonicalizeAbsolute(path, out _));
        Assert.True(PathUtil.IsProtectedRoot(path));
        Assert.False(PathUtil.IsUnder(path, @"C:\Apps"));
    }

    [Fact]
    public void ParentEscape_DoesNotEstablishOwnership()
    {
        Assert.True(PathUtil.IsProtectedRoot(@"C:\Program Files\Acme\..\..\Windows", false));
        Assert.False(PathUtil.IsUnder(@"C:\Apps\Foo\..\Foobar\job.exe", @"C:\Apps\Foo"));
        Assert.Throws<ArgumentException>(() => LeftoverScanner.FingerprintFromPath(@"C:\Program Files\Acme\..\..\Windows"));
    }

    [Fact]
    public void AnotherInstalledVersion_ProtectsItsFolderAndParent()
    {
        var target = new ProgramFingerprint { DisplayName = "PyCharm Community Edition 2024.2", KeyName = "old", Scope = RegistryScope.Machine64, InstallLocation = @"C:\Apps\JetBrains\PyCharm 2024.2" };
        var surviving = new InstalledProgram { Id = "new", KeyName = "new", Scope = RegistryScope.Machine64, RegistryPath = "unused", DisplayName = "PyCharm Community Edition 2025.2", InstallLocation = @"C:\Apps\JetBrains\PyCharm 2025.2" };
        Assert.Equal(NameNormalizer.ToKey(target.DisplayName), NameNormalizer.ToKey(surviving.DisplayName));
        Assert.True(LeftoverScanner.IsOwnedByAnotherProgram(surviving.InstallLocation, target, new[] { surviving }, true));
        Assert.True(LeftoverScanner.IsOwnedByAnotherProgram(@"C:\Apps\JetBrains", target, new[] { surviving }, true));
        Assert.False(LeftoverScanner.IsOwnedByAnotherProgram(target.InstallLocation, target, new[] { surviving }, true));
        Assert.True(NameNormalizer.HasConflictingVersion("PyCharm Community Edition 2025.2", target.DisplayName));
        Assert.False(NameNormalizer.HasConflictingVersion("PyCharm Community Edition 2024.2", target.DisplayName));
    }

    [Fact]
    public void Scanner_DoesNotSuggestSurvivingVersionOrAutoCleanNameOnlyMatches()
    {
        using var fixture = new Fixture();
        var old = fixture.CreateDirectory("JetBrains/PyCharm Community Edition 2024.2");
        var current = fixture.CreateDirectory("JetBrains/PyCharm Community Edition 2025.2");
        var survivor = new InstalledProgram { Id = "new", KeyName = "new", Scope = RegistryScope.Machine64, RegistryPath = "unused", DisplayName = "PyCharm Community Edition 2025.2", InstallLocation = current };
        var target = new ProgramFingerprint { DisplayName = "PyCharm Community Edition 2024.2", KeyName = "old", Scope = RegistryScope.Machine64, InstallLocation = old, PublisherToken = "jetbrains", NameKeys = NameNormalizer.CandidateKeys("PyCharm Community Edition 2024.2") };
        var options = new LeftoverScanOptions { ScanRegistry = false, ScanServices = false, ScanScheduledTasks = false, ScanShortcuts = false, ScanAllUserProfiles = false };
        var scan = new LeftoverScanner(() => new[] { survivor }, new[] { fixture.Root }).Scan(target, options, null, CancellationToken.None);
        Assert.Contains(scan.Items, i => PathUtil.IsUnder(i.Path, old) && i.Confidence == LeftoverConfidence.High);
        Assert.DoesNotContain(scan.Items, i => PathUtil.IsUnder(i.Path, current) || PathUtil.IsUnder(current, i.Path));

        var named = fixture.CreateDirectory("UnrelatedNameOnlyApp");
        var namesOnly = new ProgramFingerprint { DisplayName = "Unrelated Name Only App", NameKeys = NameNormalizer.CandidateKeys("Unrelated Name Only App") };
        scan = new LeftoverScanner(() => Array.Empty<InstalledProgram>(), new[] { fixture.Root }).Scan(namesOnly, options, null, CancellationToken.None);
        Assert.Contains(scan.Items, i => i.Path == PathUtil.NormalizeForCompare(named) && i.Confidence == LeftoverConfidence.Medium);
        Assert.DoesNotContain(scan.Items, i => i.Confidence == LeftoverConfidence.High);
    }

    [Fact]
    public async Task Scanner_UnreadableInventoryPreventsHighConfidenceCleanupCandidates()
    {
        using var fixture = new Fixture();
        var targetFolder = fixture.CreateDirectory("target");
        var target = new ProgramFingerprint { DisplayName = "Target app", InstallLocation = targetFolder };
        var scanner = new LeftoverScanner(() => throw new UnauthorizedAccessException("A surviving program registration is unreadable."), new[] { fixture.Root });
        var options = new LeftoverScanOptions { ScanRegistry = false, ScanServices = false, ScanScheduledTasks = false, ScanShortcuts = false };
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scanner.ScanAsync(target, options, null, CancellationToken.None));
        Assert.Contains("registration is unreadable", error.Message);
        Assert.True(Directory.Exists(targetFolder));
        // Scan must fail before returning the otherwise High-confidence existing install folder,
        // so callers cannot feed unverified ownership into automatic cleanup.
    }

    [Fact]
    public void TaskOwnership_UsesActionsAndDirectoryBoundaries()
    {
        const string prefix = "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>C:\\Apps\\Foo</Description></RegistrationInfo><Actions>";
        const string suffix = "</Actions></Task>";
        Assert.Null(LeftoverScanner.TaskOwnership(prefix + "<Exec><Command>C:\\Apps\\Foobar\\job.exe</Command></Exec>" + suffix, @"C:\Apps\Foo"));
        Assert.Equal(LeftoverConfidence.High, LeftoverScanner.TaskOwnership(prefix + "<Exec><Command>C:\\Apps\\Foo\\job.exe</Command></Exec>" + suffix, @"C:\Apps\Foo"));
        Assert.Null(LeftoverScanner.TaskOwnership(prefix + "<Exec><Command>C:\\Apps\\Foo\\job.exe</Command></Exec><Exec><Command>C:\\Apps\\Other\\job.exe</Command></Exec>" + suffix, @"C:\Apps\Foo"));
        Assert.Null(LeftoverScanner.TaskOwnership("<!DOCTYPE Task [<!ENTITY bad SYSTEM 'file:///C:/Windows/win.ini'>]><Task><Actions><Exec><Command>&bad;</Command></Exec></Actions></Task>", @"C:\Apps\Foo"));
        Assert.Null(LeftoverScanner.CommandOwnership(@"C:\Windows\host.exe", @"--file C:\Apps\Foobar\job.exe", @"C:\Apps\Foo"));
        Assert.Equal(LeftoverConfidence.Low, LeftoverScanner.CommandOwnership(@"C:\Windows\host.exe", "--file \"C:\\Apps\\Foo\\job.exe\"", @"C:\Apps\Foo"));
    }

    [Fact]
    public void RemovingUnpackedExtension_PreservesDeveloperSource()
    {
        using var fixture = new Fixture();
        var profile = fixture.CreateDirectory("profile");
        var source = fixture.CreateDirectory("developer-source");
        var sentinel = Path.Combine(source, "project.txt");
        File.WriteAllText(sentinel, "source must survive");
        const string id = "abcdefghijklmnopabcdefghijklmnop";
        var stored = Directory.CreateDirectory(Path.Combine(profile, "Extensions", id)).FullName;
        File.WriteAllText(Path.Combine(stored, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(profile, "Preferences"), "{\"extensions\":{\"settings\":{\"" + id + "\":{\"path\":\"external\"}}}}");
        var extension = new BrowserExtensionInfo { Browser = BrowserKind.Chrome, ProfileName = "test", ProfilePath = profile, Name = "Development extension", ExtensionId = id, ExtensionPath = source };
        var result = BrowserExtensionService.RemoveChromium(extension);
        Assert.True(result.Item1, result.Item2);
        Assert.Equal("source must survive", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(stored));
        Assert.Null(JsonNode.Parse(File.ReadAllText(Path.Combine(profile, "Preferences")))?["extensions"]?["settings"]?[id]);
    }

    [Fact]
    public void FailedPreferenceEdit_DoesNotClaimRemovalOrDeleteFiles()
    {
        using var fixture = new Fixture();
        var profile = fixture.CreateDirectory("profile");
        const string id = "abcdefghijklmnopabcdefghijklmnop";
        var stored = Directory.CreateDirectory(Path.Combine(profile, "Extensions", id)).FullName;
        File.WriteAllText(Path.Combine(stored, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(profile, "Preferences"), "broken json");
        var result = BrowserExtensionService.RemoveChromium(new BrowserExtensionInfo { Browser = BrowserKind.Chrome, ProfileName = "test", ProfilePath = profile, Name = "Extension", ExtensionId = id });
        Assert.False(result.Item1);
        Assert.True(Directory.Exists(stored));
    }

    [Fact]
    public void MonitorChanges_RemainUnverifiedAndPersonalDocumentsExcluded()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Root, "concurrent.txt");
        File.WriteAllText(file, "unrelated activity");
        var log = new InstallLog { Title = "Installer", InstallerPath = "unused", CreatedFiles = new() { file }, CreatedRegistryKeys = new() { @"HKCU|64|SOFTWARE\UnrelatedApp" } };
        var items = InstallMonitorService.ToLeftovers(log);
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal(LeftoverConfidence.Low, item.Confidence));
        Assert.All(items, item => Assert.Contains("unverified", item.Detail));
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
        {
            Assert.True(InstallMonitorService.IsPersonalDocument(Path.Combine(desktop, "important.txt")));
            Assert.False(InstallMonitorService.IsPersonalDocument(Path.Combine(desktop, "Application.lnk")));
        }
    }

    [Fact]
    public void Shredder_JunctionNeverExposesExternalChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var selected = fixture.CreateDirectory("selected");
        var external = fixture.CreateDirectory("external");
        var sentinel = Path.Combine(external, "sentinel.txt");
        File.WriteAllText(sentinel, "keep this file");
        File.WriteAllText(Path.Combine(selected, "owned.txt"), "erase this file");
        var junction = Path.Combine(selected, "junction");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(external);
        using var proc = Process.Start(start)!;
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
        Assert.True(PathUtil.HasReparsePoint(Path.Combine(junction, "sentinel.txt")));
        var result = new FileShredder().Shred(new[] { selected }, ShredMethod.Quick, null, CancellationToken.None);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.FilesShredded);
        Assert.False(Directory.Exists(selected));
        Assert.Equal("keep this file", File.ReadAllText(sentinel));
    }

    [Fact]
    public void Shredder_RejectsHardLinksToOtherCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var selected = fixture.CreateDirectory("selected");
        var external = fixture.CreateDirectory("external");
        var sentinel = Path.Combine(external, "sentinel.txt");
        var link = Path.Combine(selected, "hard-link.txt");
        File.WriteAllText(sentinel, "do not overwrite this copy");
        Assert.True(CreateHardLink(link, sentinel, IntPtr.Zero));
        var result = new FileShredder().Shred(new[] { link }, ShredMethod.Quick, null, CancellationToken.None);
        Assert.Single(result.Errors);
        Assert.Equal(0, result.FilesShredded);
        Assert.Equal("do not overwrite this copy", File.ReadAllText(sentinel));
        Assert.Equal("do not overwrite this copy", File.ReadAllText(link));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "evict-scanner-safety-" + Guid.NewGuid().ToString("N"))).FullName;
        public string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
        public void Dispose()
        {
            // The fixture path is fixed at creation under Temp; remove junctions before recursive cleanup.
            if (!Directory.Exists(Root)) return;
            Delete(Root);
        }
        private static void Delete(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attr = File.GetAttributes(entry);
                if ((attr & FileAttributes.Directory) != 0)
                {
                    if ((attr & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry, false);
                    else Delete(entry);
                }
                else File.Delete(entry);
            }
            Directory.Delete(directory, false);
        }
    }
}
