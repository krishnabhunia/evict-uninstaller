using Evict.Core.Services;
using Evict.Core.Util;
using Xunit;

namespace Evict.Core.Tests;

public class StartupElevationTests
{
    private static CommandLineOptions P(params string[] args) => CommandLineOptions.Parse(args);

    [Fact]
    public void Setting_is_on_by_default_also_for_old_settings_files()
    {
        Assert.True(new AppSettings().StartAsAdministrator);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Dark\",\"SettingsVersion\":2}")!;
        Assert.True(old.StartAsAdministrator);
    }

    [Fact]
    public void Elevates_a_normal_start_of_an_administrator_account()
    {
        Assert.True(StartupElevation.ShouldElevate(true, false, true, P()));
        Assert.True(StartupElevation.ShouldElevate(true, false, true, P("--uninstall-file", @"C:\Apps\x.exe")));
        Assert.True(StartupElevation.ShouldElevate(true, false, true, P("--updated")));
    }

    [Theory]
    [InlineData(false, false, true)]  // setting off
    [InlineData(true, true, true)]    // already administrator
    [InlineData(true, false, false)]  // standard account: UAC would run Evict as another user
    public void Does_not_elevate_when_off_elevated_or_standard_account(bool setting, bool elevated, bool splitToken) =>
        Assert.False(StartupElevation.ShouldElevate(setting, elevated, splitToken, P()));

    [Theory]
    [InlineData("--tray")]
    [InlineData("--scheduled-scan")]
    [InlineData("--no-elevate")]
    [InlineData("--exit")]
    [InlineData("--self-cleanup")]
    public void Hidden_starts_relaunches_and_internal_modes_never_prompt(string arg) =>
        Assert.False(StartupElevation.ShouldElevate(true, false, true, P(arg)));

    [Fact]
    public void Relaunch_keeps_arguments_and_adds_markers_once()
    {
        var args = StartupElevation.RelaunchArgs(new[] { "--uninstall-file", @"C:\Program Files\A B\a.exe", "--no-elevate", "--wait-pid", "5" }, 1234);
        Assert.Equal(new[] { "--uninstall-file", @"C:\Program Files\A B\a.exe", "--no-elevate", "--wait-pid", "1234" }, args);
        var parsed = P(args.ToArray());
        Assert.True(parsed.NoElevate);
        Assert.Equal(1234, parsed.WaitPid);
        Assert.Equal(@"C:\Program Files\A B\a.exe", parsed.UninstallFile);
    }

    [Fact]
    public void Exit_and_no_elevate_parse()
    {
        Assert.True(P("--exit").Exit);
        Assert.False(P("--exit").IsEmpty);
        Assert.True(P("/no-elevate").NoElevate);
        Assert.True(P("--no-elevate", "--wait-pid", "9").IsEmpty); // markers alone do not count as a request
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData(@"C:\Program Files\x.exe", "\"C:\\Program Files\\x.exe\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\no\space", @"C:\no\space")]
    public void Arguments_are_quoted_for_the_windows_command_line(string arg, string expected) =>
        Assert.Equal(expected, CommandLineOptions.QuoteArgument(arg));

    [Fact]
    public void Joined_arguments_split_back_the_same_way()
    {
        var args = new[] { "--uninstall-file", @"C:\Program Files\A\", "x\"y", "", "z" };
        Assert.Equal(args, SplitLikeWindows(CommandLineOptions.JoinArguments(args)));
    }

    /// <summary>CommandLineToArgvW rules (arguments after the program name).</summary>
    private static List<string> SplitLikeWindows(string cmd)
    {
        var result = new List<string>();
        int i = 0;
        while (i < cmd.Length)
        {
            while (i < cmd.Length && cmd[i] is ' ' or '\t') i++;
            if (i >= cmd.Length) break;
            var sb = new System.Text.StringBuilder();
            bool quoted = false;
            while (i < cmd.Length && (quoted || cmd[i] is not (' ' or '\t')))
            {
                int bs = 0;
                while (i < cmd.Length && cmd[i] == '\\') { bs++; i++; }
                if (i < cmd.Length && cmd[i] == '"')
                {
                    sb.Append('\\', bs / 2);
                    if (bs % 2 == 1) sb.Append('"');
                    else if (quoted && i + 1 < cmd.Length && cmd[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = !quoted;
                    i++;
                }
                else
                {
                    sb.Append('\\', bs);
                    if (i < cmd.Length && (quoted || cmd[i] is not (' ' or '\t'))) sb.Append(cmd[i++]);
                }
            }
            result.Add(sb.ToString());
        }
        return result;
    }
}
