using System.Text;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class UninstallRecoveryTests
{
    private static UninstallRunResult R(int? exit, bool launched = true, bool removed = false, bool cancelled = false, string? error = null) =>
        new() { ExitCode = exit, Launched = launched, RegistryEntryRemoved = removed, Cancelled = cancelled, Error = error };

    [Theory]
    [InlineData(0, true, false, UninstallOutcome.Succeeded)]         // entry gone
    [InlineData(0, false, false, UninstallOutcome.Succeeded)]        // entry left, files gone → orphan entry only
    [InlineData(0, false, true, UninstallOutcome.StillInstalled)]    // "finished" but nothing happened (cancelled NSIS/Inno)
    [InlineData(3010, false, true, UninstallOutcome.RebootRequired)]
    [InlineData(1641, true, false, UninstallOutcome.RebootRequired)]
    [InlineData(1602, false, true, UninstallOutcome.Cancelled)]
    [InlineData(1618, false, true, UninstallOutcome.AnotherInstallInProgress)]
    [InlineData(1605, false, true, UninstallOutcome.Succeeded)]
    [InlineData(1603, false, true, UninstallOutcome.Failed)]
    [InlineData(2, true, false, UninstallOutcome.Succeeded)]         // odd exit code, but the program is gone
    [InlineData(1603, false, false, UninstallOutcome.Succeeded)]     // failed code, entry left without files
    public void Outcome_from_exit_code_and_what_is_left(int exit, bool entryRemoved, bool stillInstalled, UninstallOutcome expected) =>
        Assert.Equal(expected, UninstallRecoveryRules.Evaluate(R(exit, removed: entryRemoved), stillInstalled));

    [Fact]
    public void Not_started_and_uac_declined()
    {
        Assert.Equal(UninstallOutcome.NotStarted, UninstallRecoveryRules.Evaluate(R(null, launched: false, error: "The uninstaller was not found: C:\\x.exe"), true));
        Assert.Equal(UninstallOutcome.Cancelled, UninstallRecoveryRules.Evaluate(R(null, launched: false, error: "The UAC prompt was cancelled."), true));
        Assert.Equal(UninstallOutcome.Cancelled, UninstallRecoveryRules.Evaluate(R(null, cancelled: true), true));
    }

    [Fact]
    public void Only_success_allows_leftover_removal_without_asking()
    {
        Assert.True(UninstallRecoveryRules.IsSuccess(UninstallOutcome.Succeeded));
        Assert.True(UninstallRecoveryRules.IsSuccess(UninstallOutcome.RebootRequired));
        foreach (var o in new[] { UninstallOutcome.Failed, UninstallOutcome.StillInstalled, UninstallOutcome.Cancelled, UninstallOutcome.NotStarted, UninstallOutcome.AnotherInstallInProgress })
            Assert.False(UninstallRecoveryRules.IsSuccess(o));
    }

    [Fact]
    public void Describe_names_the_problem()
    {
        var text = UninstallRecoveryRules.Describe(UninstallOutcome.Failed, R(1603), "Foo");
        Assert.Contains("1603", text);
        Assert.Contains("fatal error", text);
        Assert.Contains("Foo is still installed", text);
        Assert.Contains("still installed", UninstallRecoveryRules.Describe(UninstallOutcome.StillInstalled, R(0), "Foo"));
    }

    private static InstalledProgram P(string? uninstall, string? quiet = null, bool isMsi = false, string? code = null, string key = "Foo") => new()
    {
        Id = "t", KeyName = key, Scope = RegistryScope.Machine64, RegistryPath = "HKLM\\...", DisplayName = "Foo",
        UninstallString = uninstall, QuietUninstallString = quiet, IsMsi = isMsi, MsiProductCode = code,
    };

    private const string Code = "{12345678-1234-1234-1234-123456789012}";

    [Fact]
    public void After_a_silent_attempt_the_interactive_uninstaller_comes_first()
    {
        var p = P(@"""C:\Program Files\Foo\unins000.exe""", quiet: @"""C:\Program Files\Foo\unins000.exe"" /VERYSILENT");
        var used = UninstallCommandParser.Resolve(p, quiet: true)!;
        var alts = UninstallRecoveryRules.Alternatives(p, used);
        Assert.Equal("Run the uninstaller with its own window", alts[0].Label);
        Assert.DoesNotContain(alts, a => a.Command.Display == used.Display);
    }

    [Fact]
    public void Msi_product_code_is_offered_for_an_exe_uninstaller_with_a_guid_key()
    {
        var p = P(@"""C:\Program Files\Foo\setup.exe"" /remove", key: Code);
        var used = UninstallCommandParser.Resolve(p, quiet: false)!;
        var alts = UninstallRecoveryRules.Alternatives(p, used);
        Assert.Contains(alts, a => a.Command.Display.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && a.Command.Arguments.Contains(Code));
    }

    [Fact]
    public void Msi_program_after_msiexec_failed_offers_nothing_identical()
    {
        var p = P("MsiExec.exe /X" + Code, isMsi: true, code: Code, key: Code);
        var used = UninstallCommandParser.Resolve(p, quiet: false)!;
        var alts = UninstallRecoveryRules.Alternatives(p, used);
        Assert.All(alts, a => Assert.NotEqual(used.Display, a.Command.Display));
        Assert.Equal(alts.Count, alts.Select(a => a.Command.Display).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_uninstall_command_gives_no_alternatives()
    {
        Assert.Empty(UninstallRecoveryRules.Alternatives(P(null), null));
    }
}

public class RunningProgramRulesTests
{
    private static InstalledProgram P(string? loc, string? exe = null, string? icon = null) => new()
    {
        Id = "t", KeyName = "Foo", Scope = RegistryScope.Machine64, RegistryPath = "HKLM\\...", DisplayName = "Foo",
        InstallLocation = loc, PrimaryExecutable = exe, DisplayIcon = icon,
    };

    [Fact]
    public void Install_folder_and_exe_folders_are_owned_without_duplicates()
    {
        var folders = RunningProgramRules.OwnedFolders(P(@"C:\Program Files\Foo\", @"C:\Program Files\Foo\bin\foo.exe", @"""C:\Program Files\Foo\foo.exe"",0"));
        Assert.Equal(new[] { @"C:\Program Files\Foo" }, folders);
    }

    [Fact]
    public void Separate_exe_folder_is_added()
    {
        var folders = RunningProgramRules.OwnedFolders(P(@"C:\Program Files\Foo", @"C:\Users\K\AppData\Local\FooApp\foo.exe"));
        Assert.Equal(2, folders.Count);
        Assert.True(RunningProgramRules.BelongsTo(@"C:\Users\K\AppData\Local\FooApp\foo.exe", folders));
        Assert.True(RunningProgramRules.BelongsTo(@"c:\program files\foo\helper\x.exe", folders));
        Assert.False(RunningProgramRules.BelongsTo(@"C:\Program Files\FooBar\x.exe", folders));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"%ProgramFiles%\Foo")]
    [InlineData(@"\\server\share\Foo")]
    [InlineData("")]
    public void Shared_system_or_unknown_folders_are_never_owned(string folder) =>
        Assert.Empty(RunningProgramRules.OwnedFolders(P(folder, folder.Length > 0 ? folder.TrimEnd('\\') + @"\x.exe" : null)));

    [Fact]
    public void Icon_that_is_not_an_exe_is_ignored()
    {
        Assert.Empty(RunningProgramRules.OwnedFolders(P(null, null, @"C:\Program Files\Foo\foo.ico")));
    }

    [Fact]
    public void Summary_groups_processes_by_exe()
    {
        var lines = RunningProgramRules.Summarize(new (string, string?)[] { ("chrome", "Inbox – Chrome"), ("chrome", null), ("updater", null) });
        Assert.Equal(new[] { "Inbox – Chrome (chrome.exe ×2)", "updater.exe" }, lines);
    }
}

public class RecycleBinRecordTests
{
    private static byte[] V2(string path, DateTime utc)
    {
        var name = Encoding.Unicode.GetBytes(path + "\0");
        var data = new byte[28 + name.Length];
        BitConverter.GetBytes(2L).CopyTo(data, 0);
        BitConverter.GetBytes(1234L).CopyTo(data, 8);
        BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(data, 16);
        BitConverter.GetBytes(path.Length + 1).CopyTo(data, 24);
        name.CopyTo(data, 28);
        return data;
    }

    private static byte[] V1(string path, DateTime utc)
    {
        var data = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(data, 0);
        BitConverter.GetBytes(utc.ToFileTimeUtc()).CopyTo(data, 16);
        Encoding.Unicode.GetBytes(path).CopyTo(data, 24);
        return data;
    }

    [Fact]
    public void Windows_10_record()
    {
        var when = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var rec = RecycleBinService.ParseInfoRecord(V2(@"C:\Users\K\AppData\Roaming\Foo", when));
        Assert.Equal(@"C:\Users\K\AppData\Roaming\Foo", rec!.Value.Path);
        Assert.Equal(when, rec.Value.DeletedUtc);
    }

    [Fact]
    public void Vista_to_8_record()
    {
        var when = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var rec = RecycleBinService.ParseInfoRecord(V1(@"D:\Data\old.txt", when));
        Assert.Equal(@"D:\Data\old.txt", rec!.Value.Path);
        Assert.Equal(when, rec.Value.DeletedUtc);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.Null(RecycleBinService.ParseInfoRecord(new byte[10]));
        var bad = V2("x", DateTime.UtcNow); BitConverter.GetBytes(7L).CopyTo(bad, 0);
        Assert.Null(RecycleBinService.ParseInfoRecord(bad));
    }

    [Fact]
    public void Undo_remembers_only_recycled_files_that_were_removed()
    {
        var undo = new CleanupUndo();
        undo.BeginIfFirst();
        Assert.False(undo.CanUndo);
        var ok = new LeftoverItem { Kind = LeftoverKind.Folder, Path = @"C:\Foo" };
        var failed = new LeftoverItem { Kind = LeftoverKind.File, Path = @"C:\Bar.txt" };
        var result = new CleanupResult { RegistryBackupFile = @"C:\b.reg" };
        result.Errors.Add((failed, "in use"));
        undo.Record(new[] { ok, failed }, result, sentToRecycleBin: false);
        Assert.True(undo.CanUndo);                 // the registry backup alone can be undone
        Assert.Equal(@"C:\b.reg", undo.RegistryBackupFile);
        undo.Record(new[] { ok }, new CleanupResult { RegistryBackupFile = @"C:\second.reg" }, sentToRecycleBin: true);
        Assert.Equal(@"C:\b.reg", undo.RegistryBackupFile); // the first backup is kept
    }
}
