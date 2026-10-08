using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class SelfCleanupPortableNameTests
{
    [Theory]
    [InlineData(@"C:\Tools\Evict.exe", true)]
    [InlineData("Evict.old.exe", true)]
    [InlineData("Evict.old.1234.exe", true)]
    [InlineData(@"C:\Tools\Evict_1.12.2.exe", true)]
    [InlineData(@"C:\Tools\Evict_1.12.2-beta.44.16.1.exe", true)]
    [InlineData("EVICT_0.0.0.EXE", true)]
    [InlineData("Evict_1.12.exe", false)]
    [InlineData("Evict_01.12.2.exe", false)]
    [InlineData("Evict_1.12.02.exe", false)]
    [InlineData("Evict_1.12.2-beta.044.16.1.exe", false)]
    [InlineData("Evict_1.12.2-beta.44.0.1.exe", false)]
    [InlineData("Evict_1.12.2-beta.44.16.0.exe", false)]
    [InlineData("Evict_1.12.2-beta.1.exe", false)]
    [InlineData("Evict_1.12.2-rc.44.16.1.exe", false)]
    [InlineData("Evict_1.12.2+build.exe", false)]
    [InlineData("Evict_1.12.2.exe.backup", false)]
    [InlineData("OtherEvict_1.12.2.exe", false)]
    [InlineData("Evict.old-tool.exe", false)]
    [InlineData("Evict.old.0.exe", false)]
    [InlineData("Evict.old.0123.exe", false)]
    public void Executable_identity_accepts_only_packaged_and_update_backup_names(string name, bool expected) =>
        Assert.Equal(expected, SelfCleanupService.IsEvictExe(name));

    [Theory]
    [InlineData("Evict_1.12.2.exe.1234.dmp", true)]
    [InlineData("Evict_1.12.2-beta.44.16.1.exe.1234.dmp", true)]
    [InlineData("Evict.exe.1234.dmp", true)]
    [InlineData("Evict.old.1234.exe.5678.dmp", true)]
    [InlineData("Evict_1.12.exe.1234.dmp", false)]
    [InlineData("Evict_1.12.2.exe.notes.dmp", false)]
    [InlineData("Evict_1.12.2.exe.0.dmp", false)]
    [InlineData("OtherEvict_1.12.2.exe.1234.dmp", false)]
    [InlineData("Evict_1.12.2.exe.1234.dmp.backup", false)]
    public void Crash_dump_identity_preserves_other_dump_files(string name, bool expected) =>
        Assert.Equal(expected, SelfCleanupService.IsEvictCrashDump(name));

    [Theory]
    [InlineData("EVICT_1.12.2.EXE-A1B2C3D4.pf", true)]
    [InlineData("EVICT_1.12.2-BETA.44.16.1.EXE-A1B2C3D4.pf", true)]
    [InlineData("EVICT.EXE-A1B2C3D4.pf", true)]
    [InlineData("EVICT_1.12.EXE-A1B2C3D4.pf", false)]
    [InlineData("EVICT_1.12.2.EXE-ABCDEFGH.pf", false)]
    [InlineData("EVICT_1.12.2.EXE-A1B2C3D4.pf.backup", false)]
    [InlineData("OTHEREVICT_1.12.2.EXE-A1B2C3D4.pf", false)]
    public void Prefetch_identity_requires_the_complete_host_and_hash(string name, bool expected) =>
        Assert.Equal(expected, SelfCleanupService.IsEvictPrefetch(name));

    [Theory]
    [InlineData("Evict Uninstaller", true)]
    [InlineData(@"C:\Tools\Evict.exe", true)]
    [InlineData(@"C:\Tools\Evict_1.12.2.exe", true)]
    [InlineData(@"C:\Tools\Evict_1.12.2-beta.44.16.1.exe", true)]
    [InlineData("OtherEvict.exe", false)]
    [InlineData("Evict.exe.backup", false)]
    [InlineData(@"C:\Tools\Evict_1.12.2.exe.backup", false)]
    public void Notification_identity_does_not_match_an_executable_substring(string name, bool expected) =>
        Assert.Equal(expected, SelfCleanupService.IsEvictNotificationKey(name));

    [Theory]
    [InlineData("Evict", true)]
    [InlineData("Evict_1.12.2", true)]
    [InlineData("Evict_1.12.2-beta.44.16.1", true)]
    [InlineData("Evict_1.12", false)]
    [InlineData("Evict_1.12.2.exe", false)]
    [InlineData("Evict_1.12.2-beta.44.16.1.backup", false)]
    [InlineData("Evict_1.12.2-rc.1", false)]
    [InlineData("Evict.old", false)]
    [InlineData("Evict_notes", false)]
    public void Extraction_identity_is_the_exact_runtime_host_stem(string name, bool expected) =>
        Assert.Equal(expected, SelfCleanupService.IsEvictExtractionName(name));

    [Fact]
    public void Custom_extraction_base_and_unrelated_host_caches_are_preserved()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "EvictPortableCacheTests-" + Guid.NewGuid().ToString("N"));
        var cacheBase = Path.Combine(workspace, "custom-base");
        var expected = new[] { "Evict", "Evict_1.12.2", "Evict_1.12.2-beta.44.16.1" };
        var decoys = new[] { "OtherApp", "Evict_1.12", "Evict_1.12.2.exe", "Evict_1.12.2-beta.44.16.1.backup" };
        try
        {
            foreach (var name in expected.Concat(decoys))
            {
                var bundle = Path.Combine(cacheBase, name, "isolated-bundle");
                Directory.CreateDirectory(bundle);
                File.WriteAllText(Path.Combine(bundle, "fixture.txt"), name);
            }
            var baseSentinel = Path.Combine(cacheBase, "keep.txt");
            File.WriteAllText(baseSentinel, "keep custom base");
            var report = new SelfCleanupReport();

            SelfCleanupService.RemoveExtractionCaches(cacheBase, report);

            Assert.Empty(report.Failed);
            Assert.All(expected, name => Assert.False(Directory.Exists(Path.Combine(cacheBase, name))));
            Assert.All(decoys, name => Assert.Equal(name,
                File.ReadAllText(Path.Combine(cacheBase, name, "isolated-bundle", "fixture.txt"))));
            Assert.Equal("keep custom base", File.ReadAllText(baseSentinel));
            Assert.True(Directory.Exists(cacheBase));
        }
        finally
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
    }
}
