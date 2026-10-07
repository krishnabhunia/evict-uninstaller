using System.Diagnostics;
using System.Text;
using Xunit;

namespace Evict.Core.Tests;

public sealed class PublishReliabilityTests
{
    [Theory]
    [InlineData(17, 0, "Unit tests failed", false)]
    [InlineData(0, 19, "Publishing failed", true)]
    public async Task FailedNativeCommandsStopBeforeTheNextPackagingStage(int testExitCode, int publishExitCode, string expectedError, bool publishExpected)
    {
        if (!OperatingSystem.IsWindows()) return;
        var temporary = Path.Combine(Path.GetTempPath(), "EvictPublishTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, "build"));
        Directory.CreateDirectory(Path.Combine(temporary, "Portable"));
        var marker = Path.Combine(temporary, "Portable", "previous-output.txt");
        File.WriteAllText(marker, "existing publish output");
        var script = Path.Combine(temporary, "build", "publish.ps1");
        File.Copy(FindPublishScript(), script);
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var calls = Path.Combine(temporary, "publish-called.txt");
        var error = Path.Combine(temporary, "error.txt");
        var command = $$"""
            function global:dotnet {
                if ($args[0] -eq 'test') { $global:LASTEXITCODE = {{testExitCode}}; return }
                if ($args[0] -eq 'publish') {
                    'called' | Set-Content -LiteralPath {{Quote(calls)}}
                    $global:LASTEXITCODE = {{publishExitCode}}
                    return
                }
                throw 'Unexpected dotnet command'
            }
            try { & {{Quote(script)}}; exit 10 }
            catch { $_.Exception.Message | Set-Content -LiteralPath {{Quote(error)}}; exit 0 }
            """;
        try
        {
            Assert.Equal(0, await RunPowerShellAsync(command));
            Assert.Contains(expectedError, File.ReadAllText(error));
            Assert.Equal(publishExpected, File.Exists(calls));
            Assert.Equal(!publishExpected, File.Exists(marker));
            Assert.False(File.Exists(Path.Combine(temporary, "Portable", "Evict.exe.sha256")));
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    [Fact]
    public async Task ExistingPublishOutputJunctionIsRefusedAndItsTargetIsPreserved()
    {
        if (!OperatingSystem.IsWindows()) return;
        var temporary = Path.Combine(Path.GetTempPath(), "EvictPublishTests-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(temporary, "project");
        var target = Path.Combine(temporary, "outside-output");
        var portable = Path.Combine(project, "Portable");
        Directory.CreateDirectory(Path.Combine(project, "build"));
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "preserve.txt");
        File.WriteAllText(marker, "must survive");
        var script = Path.Combine(project, "build", "publish.ps1");
        File.Copy(FindPublishScript(), script);
        string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var calls = Path.Combine(temporary, "publish-called.txt");
        var error = Path.Combine(temporary, "error.txt");
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            New-Item -ItemType Junction -Path {{Quote(portable)}} -Target {{Quote(target)}} | Out-Null
            function global:dotnet {
                if ($args[0] -eq 'test') { $global:LASTEXITCODE = 0; return }
                if ($args[0] -eq 'publish') { 'called' | Set-Content -LiteralPath {{Quote(calls)}}; throw 'Publish should not run' }
                throw 'Unexpected dotnet command'
            }
            try { & {{Quote(script)}}; exit 10 }
            catch { $_.Exception.Message | Set-Content -LiteralPath {{Quote(error)}}; exit 0 }
            """;
        try
        {
            Assert.Equal(0, await RunPowerShellAsync(command));
            Assert.Contains("Refusing to replace", File.ReadAllText(error));
            Assert.False(File.Exists(calls));
            Assert.Equal("must survive", File.ReadAllText(marker));
            Assert.True(Directory.Exists(portable));
        }
        finally
        {
            // Remove only the fixture junction before recursively deleting this fixture's own directories.
            if (Directory.Exists(portable)) Directory.Delete(portable);
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task<int> RunPowerShellAsync(string command)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-EncodedCommand");
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var diagnostics = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new TimeoutException("Isolated publish fixture timed out"); }
        await Task.WhenAll(output, diagnostics);
        return process.ExitCode;
    }

    private static string FindPublishScript()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "build", "publish.ps1");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("The publish script was not found above the test output directory.");
    }
}
