using Evict.Core.Models;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public sealed class ProcessOwnershipTests
{
    private const string Vendor = @"C:\EvictProcessScopeTests\Vendor";
    private static InstalledProgram Program(string key, string? folder, string? exe = null, string? icon = null) => new()
    {
        Id = key, KeyName = key, Scope = RegistryScope.Machine64, RegistryPath = "HKLM\\...", DisplayName = key,
        InstallLocation = folder, PrimaryExecutable = exe, DisplayIcon = icon,
    };

    [Fact]
    public void Shared_folder_never_authorizes_a_siblings_process_or_a_guessed_executable()
    {
        var a = Program("A", Vendor, Vendor + @"\b.exe");
        var b = Program("B", Vendor, Vendor + @"\b.exe");
        var scope = RunningProgramRules.CreateScope(a, null, new[] { a, b });
        Assert.Empty(scope.Folders);
        Assert.Empty(scope.Executables);
        Assert.False(scope.Contains(Vendor + @"\b.exe"));
    }

    [Fact]
    public void Explicit_icon_in_shared_folder_authorizes_only_that_executable()
    {
        var a = Program("A", Vendor, Vendor + @"\a.exe", "\"" + Vendor + "\\a.exe\",0");
        var b = Program("B", Vendor, Vendor + @"\b.exe");
        var scope = RunningProgramRules.CreateScope(a, null, new[] { a, b });
        Assert.Empty(scope.Folders);
        Assert.True(scope.Contains(Vendor + @"\a.exe"));
        Assert.False(scope.Contains(Vendor + @"\b.exe"));
        Assert.False(scope.Contains(Vendor + @"\helper.exe"));
    }

    [Fact]
    public void Executable_claimed_by_a_surviving_program_is_not_authorized()
    {
        var a = Program("A", Vendor, icon: Vendor + @"\shared.exe");
        var b = Program("B", Vendor, Vendor + @"\shared.exe");
        var scope = RunningProgramRules.CreateScope(a, null, new[] { a, b });
        Assert.False(scope.Contains(Vendor + @"\shared.exe"));
    }

    [Theory]
    [InlineData(@"C:\EvictProcessScopeTests\Vendor\B")]
    [InlineData(@"C:\EvictProcessScopeTests")]
    public void Parent_or_child_survivor_registration_blocks_broad_scope(string survivingFolder)
    {
        var a = Program("A", Vendor);
        var b = Program("B", survivingFolder);
        Assert.Empty(RunningProgramRules.CreateScope(a, null, new[] { a, b }).Folders);
    }

    [Fact]
    public void Explicitly_selected_executable_does_not_expand_to_its_parent_folder()
    {
        var b = Program("B", Vendor, Vendor + @"\b.exe");
        var scope = RunningProgramRules.CreateScope(null, Vendor + @"\a.exe", new[] { b });
        Assert.Empty(scope.Folders);
        Assert.True(scope.Contains(Vendor + @"\a.exe"));
        Assert.False(scope.Contains(Vendor + @"\b.exe"));
    }

    [Fact]
    public void Exclusive_selected_registered_folder_can_be_closed_but_shared_selected_folder_cannot()
    {
        var a = Program("A", Vendor, Vendor + @"\a.exe");
        var b = Program("B", Vendor, Vendor + @"\b.exe");
        Assert.True(RunningProgramRules.CreateScope(null, Vendor, new[] { a }).Contains(Vendor + @"\a.exe"));
        Assert.False(RunningProgramRules.CreateScope(null, Vendor, new[] { a, b }).Contains(Vendor + @"\b.exe"));
    }

    [Fact]
    public void Canonical_parent_segments_cannot_bypass_shared_ownership()
    {
        var a = Program("A", Vendor + @"\Unused\..");
        var b = Program("B", Vendor);
        Assert.Empty(RunningProgramRules.CreateScope(a, null, new[] { a, b }).Folders);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\EvictProcessScopeTests\Vendor.\")]
    [InlineData(@"\\server\share\Application")]
    [InlineData(@"Vendor\Application")]
    public void Protected_or_ambiguous_target_paths_are_not_process_scopes(string path)
    {
        var scope = RunningProgramRules.CreateScope(null, path, Array.Empty<InstalledProgram>());
        Assert.Empty(scope.Folders);
        Assert.Empty(scope.Executables);
    }

    [Fact]
    public void Unqueryable_or_reused_pid_executable_does_not_confirm_identity()
    {
        Assert.False(RunningProgramRules.SameExecutable(null, Vendor + @"\a.exe"));
        Assert.False(RunningProgramRules.SameExecutable(Vendor + @"\b.exe", Vendor + @"\a.exe"));
        Assert.True(RunningProgramRules.SameExecutable(Vendor.ToLowerInvariant() + @"\a.exe", Vendor + @"\a.exe"));
    }
}
