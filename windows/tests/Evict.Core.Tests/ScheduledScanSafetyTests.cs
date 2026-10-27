using System.Globalization;
using System.Xml.Linq;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

public sealed class ScheduledScanSafetyTests : IDisposable
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private const string OtherSid = "S-1-5-21-1-2-3-1002";
    private const string Exe = @"C:\Tools\Evict.exe";
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "Evict-schedule-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, ScheduledTaskRead> _tasks = new();
    private readonly List<string> _commands = new();
    private string Name => ScheduledScanTask.TaskNameForUser(Sid);
    private string Definition => Path.Combine(_temp, "schedule.xml");
    private Func<string, ProcessResult?>? _beforeCommand;
    private Action<string>? _afterCommand;
    private Func<string, ScheduledTaskRead?>? _readOverride;

    public ScheduledScanSafetyTests() => Directory.CreateDirectory(_temp);
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }

    private ScheduledScanService Service() => new(() => Sid, (args, _) =>
    {
        _commands.Add(args);
        var intercepted = _beforeCommand?.Invoke(args);
        if (intercepted != null) return Task.FromResult(intercepted);
        var name = System.Text.RegularExpressions.Regex.Match(args, "/TN \"([^\"]+)\"").Groups[1].Value;
        if (args.StartsWith("/Create"))
        {
            var xml = File.ReadAllText(Definition);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var enabled = XDocument.Parse(xml).Root!.Element(ns + "Settings")!.Element(ns + "Enabled")!.Value == "true";
            _tasks[name] = new(true, xml, enabled);
        }
        else if (args.StartsWith("/Delete")) _tasks.Remove(name);
        else throw new InvalidOperationException("Unexpected task mutation: " + args);
        _afterCommand?.Invoke(args);
        return Task.FromResult(new ProcessResult());
    }, Definition, Exe, (name, _) => Task.FromResult(_readOverride?.Invoke(name) ?? _tasks.GetValueOrDefault(name) ?? ScheduledTaskRead.Missing));

    private static ScheduledTaskRead DefinitionFor(string mode = "Daily", int hour = 8, int day = 0, string sid = Sid, string exe = Exe) =>
        new(true, ScheduledScanTask.BuildTaskXml(exe, mode, hour, day, sid, new DateTime(2026, 10, 27, 12, 0, 0)), true);

    [Theory]
    [InlineData("Daily", 8, 0, "2026-10-28T08:00:00")]
    [InlineData("Daily", 15, 0, "2026-10-27T15:00:00")]
    [InlineData("Weekly", 10, 2, "2026-11-03T10:00:00")]
    [InlineData("Weekly", 10, 4, "2026-10-29T10:00:00")]
    public void NewScheduleStartsAtNextFutureSlot(string mode, int hour, int day, string expected)
    {
        var xml = DefinitionFor(mode, hour, day).Xml!;
        Assert.Contains("<StartBoundary>" + expected + "</StartBoundary>", xml);
    }

    [Fact]
    public void BoundaryFormattingIsIndependentOfCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.Contains("2026-10-28T08:00:00", DefinitionFor().Xml);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Weekly")]
    public async Task ApplyVerifiesTheTaskBeforeSaving(string mode)
    {
        var service = Service();
        var saved = false;
        var result = await service.ApplyAndSaveAsync(mode, 10, 2, () =>
        {
            saved = true;
            Assert.True(ScheduledScanTask.Matches(_tasks[Name].Xml!, true, Exe, mode, 10, 2, Sid));
            return (true, null);
        }, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.True(saved);
        Assert.True(await service.MatchesAsync(mode, 10, 2, CancellationToken.None));
        Assert.False(File.Exists(Definition));
    }

    [Fact]
    public async Task OffVerifiesAbsenceBeforeSaving()
    {
        _tasks[Name] = DefinitionFor();
        _tasks[ScheduledScanTask.LegacyTaskName] = DefinitionFor();
        var saved = false;
        var result = await Service().ApplyAndSaveAsync("Off", 10, 0, () =>
        {
            saved = true;
            Assert.Empty(_tasks);
            return (true, null);
        }, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.True(saved);
    }

    [Fact]
    public async Task BlockedCreationPreservesPreviousTaskAndDoesNotSave()
    {
        var before = DefinitionFor();
        _tasks[Name] = before;
        _beforeCommand = _ => new ProcessResult { ExitCode = 1, StdErr = "operation blocked" };
        var saved = false;
        var result = await Service().ApplyAndSaveAsync("Weekly", 10, 2, () => { saved = true; return (true, null); }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("blocked", result.Error);
        Assert.False(saved);
        Assert.Equal(before, _tasks[Name]);
        Assert.Single(_commands);
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Off")]
    public async Task FailedSettingsSaveRestoresPreviousCurrentAndLegacyTasks(string mode)
    {
        var before = DefinitionFor();
        _tasks[Name] = before;
        _tasks[ScheduledScanTask.LegacyTaskName] = before;
        var result = await Service().ApplyAndSaveAsync(mode, 10, 1, () => (false, "disk full"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("disk full", result.Error);
        Assert.Equal(before, _tasks[Name]);
        Assert.Equal(before, _tasks[ScheduledScanTask.LegacyTaskName]);
        Assert.DoesNotContain("Recovery could not", result.Error);
    }

    [Fact]
    public async Task FailedFirstSaveRemovesTheNewTask()
    {
        var result = await Service().ApplyAndSaveAsync("Daily", 10, 0, () => (false, "disk full"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Empty(_tasks);
        Assert.Equal(2, _commands.Count);
    }

    [Fact]
    public async Task ReportedSuccessWithWrongDefinitionIsRejectedAndRolledBack()
    {
        var before = DefinitionFor();
        _tasks[Name] = before;
        var mutate = true;
        _afterCommand = args =>
        {
            if (mutate && args.StartsWith("/Create"))
            {
                mutate = false;
                _tasks[Name] = DefinitionFor("Daily", 11);
            }
        };
        var saved = false;
        var result = await Service().ApplyAndSaveAsync("Daily", 10, 0, () => { saved = true; return (true, null); }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.False(saved);
        Assert.Equal(before, _tasks[Name]);
    }

    [Fact]
    public async Task FailedRollbackReportsIncompleteRecovery()
    {
        _tasks[Name] = DefinitionFor();
        var writes = 0;
        _beforeCommand = args => args.StartsWith("/Create") && ++writes > 1
            ? new ProcessResult { ExitCode = 1, StdErr = "restore blocked" } : null;
        var result = await Service().ApplyAndSaveAsync("Daily", 10, 0, () => (false, "disk full"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("Recovery could not be confirmed", result.Error);
        Assert.Contains("restore blocked", result.Error);
    }

    [Fact]
    public async Task UnknownTaskStateIsNeverTreatedAsAbsent()
    {
        _readOverride = _ => new(false, Error: "access denied");
        var service = Service();
        Assert.False(await service.MatchesAsync("Off", 10, 0, CancellationToken.None));
        var result = await service.ApplyAsync("Daily", 10, 0, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("access denied", result.Error);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task UnknownReadbackDoesNotBlindlyOverwriteAChangedTask()
    {
        _tasks[Name] = DefinitionFor();
        var writes = 0;
        _afterCommand = _ => writes++;
        _readOverride = name => name == Name && writes > 0 ? new(false, Error: "query blocked") : null;
        var result = await Service().ApplyAsync("Daily", 10, 0, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("Recovery could not be confirmed", result.Error);
        Assert.Single(_commands);
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Off")]
    public async Task AnotherUsersLegacyTaskIsNeverRemoved(string mode)
    {
        var foreign = DefinitionFor(sid: OtherSid);
        _tasks[ScheduledScanTask.LegacyTaskName] = foreign;
        var result = await Service().ApplyAsync(mode, 10, 0, CancellationToken.None);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(foreign, _tasks[ScheduledScanTask.LegacyTaskName]);
        Assert.DoesNotContain(_commands, command => command.Contains("/TN \"" + ScheduledScanTask.LegacyTaskName + "\""));
    }

    [Fact]
    public async Task ForeignCurrentTaskIsNeverOverwritten()
    {
        _tasks[Name] = DefinitionFor(exe: @"C:\Other\Other.exe");
        Assert.False((await Service().ApplyAsync("Daily", 10, 0, CancellationToken.None)).Ok);
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task ConcurrentForeignEditDuringSaveFailureIsPreserved()
    {
        var foreign = DefinitionFor(exe: @"C:\Other\Other.exe");
        _tasks[Name] = DefinitionFor();
        var result = await Service().ApplyAndSaveAsync("Daily", 10, 0, () =>
        {
            _tasks[Name] = foreign;
            return (false, "save failed");
        }, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("Recovery could not", result.Error);
        Assert.Equal(foreign, _tasks[Name]);
        Assert.Single(_commands);
    }

    [Fact]
    public async Task ReadOnlyStatusNeverMutatesOrMigratesTasks()
    {
        _tasks[ScheduledScanTask.LegacyTaskName] = DefinitionFor();
        var service = Service();
        Assert.False(await service.MatchesAsync("Daily", 8, 0, CancellationToken.None));
        Assert.False(await service.MatchesAsync("Off", 8, 0, CancellationToken.None));
        Assert.Empty(_commands);
        Assert.False(File.Exists(Definition));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("identity")]
    [InlineData("executable")]
    [InlineData("arguments")]
    [InlineData("principal")]
    [InlineData("hour")]
    [InlineData("frequency")]
    [InlineData("extra-trigger")]
    [InlineData("extra-action")]
    public async Task MismatchedTasksCannotAuthorizeBackgroundScans(string mismatch)
    {
        var state = DefinitionFor();
        var xml = state.Xml!;
        xml = mismatch switch
        {
            "identity" => xml.Replace(Sid, OtherSid),
            "executable" => xml.Replace(Exe, @"C:\Other\Other.exe"),
            "arguments" => xml.Replace("--scheduled-scan", "--tray"),
            "principal" => xml.Replace("LeastPrivilege", "HighestAvailable"),
            "hour" => xml.Replace("T08:00:00", "T09:00:00"),
            "frequency" => xml.Replace("<DaysInterval>1", "<DaysInterval>2"),
            "extra-trigger" => xml.Replace("</Triggers>", "<LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>"),
            "extra-action" => xml.Replace("</Actions>", "<Exec><Command>Other.exe</Command></Exec></Actions>"),
            _ => xml
        };
        _tasks[Name] = state with { Xml = xml, Enabled = mismatch != "disabled" };
        Assert.False(await Service().MatchesAsync("Daily", 8, 0, CancellationToken.None));
        Assert.Empty(_commands);
    }

    [Fact]
    public async Task CancellationAfterMutationRestoresWithAFreshToken()
    {
        using var cancellation = new CancellationTokenSource();
        var before = DefinitionFor();
        _tasks[Name] = before;
        _afterCommand = _ => cancellation.Cancel();
        var saved = false;
        var result = await Service().ApplyAndSaveAsync("Daily", 10, 0, () => { saved = true; return (true, null); }, cancellation.Token);
        Assert.False(result.Ok);
        Assert.False(saved);
        Assert.Equal(before, _tasks[Name]);
    }

    [Fact]
    public async Task ConcurrentAppliesAreSerializedIncludingTheSettingsSave()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var xmls = new List<string>();
        var states = new Dictionary<string, ScheduledTaskRead>();
        var service = new ScheduledScanService(() => Sid, async (args, _) =>
        {
            var xml = File.ReadAllText(Definition);
            xmls.Add(xml);
            if (xmls.Count == 1)
            {
                entered.SetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(xml, File.ReadAllText(Definition));
            }
            states[Name] = new(true, xml, true);
            return new ProcessResult();
        }, Definition, Exe, (name, _) => Task.FromResult(states.GetValueOrDefault(name) ?? ScheduledTaskRead.Missing));
        var saved = new List<string>();
        var first = service.ApplyAndSaveAsync("Daily", 8, 0, () => { saved.Add("Daily"); return (true, null); }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ApplyAndSaveAsync("Weekly", 9, 2, () => { saved.Add("Weekly"); return (true, null); }, CancellationToken.None);
        Assert.Single(xmls);
        release.SetResult();
        Assert.True((await first).Ok);
        Assert.True((await second).Ok);
        Assert.Equal(new[] { "Daily", "Weekly" }, saved);
        Assert.Equal(2, xmls.Count);
    }
    [Fact]
    public async Task NativeWindowsReaderRecognizesMissingTaskWithoutParsingErrorMessages()
    {
        if (!OperatingSystem.IsWindows()) return;
        var state = await ScheduledScanService.ReadNativeAsync("Evict-read-only-test-" + Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.True(state.IsKnown, state.Error);
        Assert.False(state.Exists);
    }

    [Fact]
    public void WindowsTaskDefinitionAcceptsAndPreservesTheRequestedSchedule()
    {
        if (!OperatingSystem.IsWindows()) return;
        object? service = null, definition = null, settings = null;
        try
        {
            var sid = ScheduledScanService.CurrentUserSid();
            var type = Type.GetTypeFromProgID("Schedule.Service")!;
            service = Activator.CreateInstance(type)!;
            ((dynamic)service).Connect();
            // In-memory XML validation only. Never register a task on the test machine.
            definition = ((dynamic)service).NewTask(0);
            foreach (var mode in new[] { "Daily", "Weekly" })
            {
                ((dynamic)definition).XmlText = ScheduledScanTask.BuildTaskXml(Exe, mode, 10, 2, sid);
                var xml = (string)((dynamic)definition).XmlText;
                settings = ((dynamic)definition).Settings;
                var enabled = (bool)((dynamic)settings).Enabled;
                Assert.True(ScheduledScanTask.Matches(xml, enabled, Exe, mode, 10, 2, sid), xml);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(settings);
                settings = null;
            }
        }
        finally
        {
            foreach (var instance in new[] { settings, definition, service })
                if (instance != null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
        }
    }

}
