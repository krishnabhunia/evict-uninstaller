using Evict.Core.Models;
using Evict.Core.Services;
using Xunit;

namespace Evict.Core.Tests;

[CollectionDefinition("Tool service safety", DisableParallelization = true)]
public sealed class ToolServiceSafetyCollection { }

[Collection("Tool service safety")]
public sealed class ToolsSafetyTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "Evict-tool-tests-" + Guid.NewGuid().ToString("N"));
    private readonly bool _loggingWasEnabled;
    private string RecordFile => Path.Combine(_temp, "hibernation.json");

    public ToolsSafetyTests()
    {
        Directory.CreateDirectory(_temp);
        _loggingWasEnabled = Log.Enabled;
        Log.Enabled = false; // Fault tests must not create logs in the real user's app-data folder.
    }

    public void Dispose()
    {
        Log.Enabled = _loggingWasEnabled;
        var resolved = Path.GetFullPath(_temp);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }

    private static List<(CleanupGroup Group, CleanupItem Item)> UpdateCacheSelection() => new()
    {
        (new CleanupGroup { Category = CleanupCategory.UpdateCache, Title = "Update cache", Description = "Test", SpecialAction = "ms-settings:test" },
            new CleanupItem { Path = "unused-test-path", Size = 7 })
    };

    [Fact]
    public async Task CleanupCancellationRestoresBothOriginallyRunningServicesWithFreshTokens()
    {
        using var cancelled = new CancellationTokenSource();
        var commands = new List<string>();
        var service = new SystemCleanupService(() => true, _ => true, (verb, name, token) =>
        {
            commands.Add(verb + " " + name);
            if (verb == "stop" && name == "bits") cancelled.Cancel();
            if (verb == "start") Assert.False(token.IsCancellationRequested);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanAsync(UpdateCacheSelection(), false, null, cancelled.Token));
        Assert.Equal(new[] { "stop wuauserv", "stop bits", "start bits", "start wuauserv" }, commands);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CleanupPreservesOriginallyStoppedServices(bool windowsUpdateRunning, bool bitsRunning)
    {
        var commands = new List<string>();
        var service = new SystemCleanupService(() => true, n => n == "bits" ? bitsRunning : windowsUpdateRunning,
            (verb, name, _) => { commands.Add(verb + " " + name); return Task.CompletedTask; });
        await service.CleanAsync(UpdateCacheSelection(), false, null, CancellationToken.None);
        Assert.Equal(windowsUpdateRunning ? 2 : 0, commands.Count(c => c.EndsWith("wuauserv")));
        Assert.Equal(bitsRunning ? 2 : 0, commands.Count(c => c.EndsWith("bits")));
    }

    [Fact]
    public async Task CleanupStopFailureStillRestoresEachAttemptedService()
    {
        var commands = new List<string>();
        var service = new SystemCleanupService(() => true, _ => true, (verb, name, _) =>
        {
            commands.Add(verb + " " + name);
            if (verb == "stop" && name == "bits") throw new IOException("stop failed");
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync<IOException>(() => service.CleanAsync(UpdateCacheSelection(), false, null, CancellationToken.None));
        Assert.Equal(new[] { "stop wuauserv", "stop bits", "start bits", "start wuauserv" }, commands);
    }

    [Fact]
    public async Task CleanupReportsRecoveryFailureAndStillRestoresOtherService()
    {
        var commands = new List<string>();
        var service = new SystemCleanupService(() => true, _ => true, (verb, name, _) =>
        {
            commands.Add(verb + " " + name);
            if (verb == "start" && name == "bits") throw new IOException("start failed");
            return Task.CompletedTask;
        });
        var result = await service.CleanAsync(UpdateCacheSelection(), false, null, CancellationToken.None);
        Assert.Equal(1, result.Failed);
        Assert.Contains(result.Errors, e => e.Contains("restore bits"));
        Assert.Contains("start wuauserv", commands);
    }

    [Fact]
    public async Task CleanupCancellationStillSurfacesRecoveryFailures()
    {
        using var cancelled = new CancellationTokenSource();
        var commands = new List<string>();
        var service = new SystemCleanupService(() => true, _ => true, (verb, name, token) =>
        {
            commands.Add(verb + " " + name);
            if (verb == "stop" && name == "bits") cancelled.Cancel();
            if (verb == "start") Assert.False(token.IsCancellationRequested);
            if (verb == "start" && name == "bits") throw new IOException("restore denied");
            return Task.CompletedTask;
        });
        var error = await Assert.ThrowsAsync<AggregateException>(() => service.CleanAsync(UpdateCacheSelection(), false, null, cancelled.Token));
        Assert.Contains(error.InnerExceptions, e => e is OperationCanceledException);
        Assert.Contains("Could not restore bits: restore denied", error.Message);
        Assert.Equal(new[] { "stop wuauserv", "stop bits", "start bits", "start wuauserv" }, commands);
    }

    [Fact]
    public async Task CleanupDoesNotClaimDiskSpaceForRecycledOrAlreadyGoneItems()
    {
        var group = new CleanupGroup { Category = CleanupCategory.InstallationFiles, Title = "Setups", Description = "Test", UseRecycleBin = true };
        var service = new SystemCleanupService(() => false, _ => throw new Exception("No services expected"),
            (_, _, _) => throw new Exception("No services expected"),
            (path, _, _, _) => path == "recycled" ? new(null, Removed: true, Recycled: true) : new(null));
        var selection = new[] { (group, new CleanupItem { Path = "recycled", Size = 100 }), (group, new CleanupItem { Path = "gone", Size = 50 }) };
        var result = await service.CleanAsync(selection, false, null, CancellationToken.None);
        Assert.Equal(1, result.Removed);
        Assert.Equal(100, result.BytesRecycled);
        Assert.Equal(0, result.BytesFreed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AntivirusProviderHealth.NotMonitored)]
    [InlineData(AntivirusProviderHealth.Poor)]
    [InlineData(AntivirusProviderHealth.Snooze)]
    public void RegisteredAntivirusNeverSuppressesDefenderOffWithoutHealthyProvider(AntivirusProviderHealth? health)
    {
        var state = SecurityRules.ParseDefender("""{"AntivirusEnabled":false,"RealTime":false,"OtherAv":["Expired trial antivirus"]}""", health);
        var findings = SecurityRules.DefenderFindings(state).ToList();
        Assert.Contains(findings, f => f.Title == "Real-time protection is off");
        Assert.DoesNotContain(findings, f => f.Title.Contains("protection healthy") || f.Title.StartsWith("Protected by"));
    }

    [Fact]
    public void HealthyCategoryCanReportPassiveDefenderWithoutCertifyingRegisteredNames()
    {
        var state = SecurityRules.ParseDefender("""{"AntivirusEnabled":false,"RealTime":false,"OtherAv":["Off product","Active product"]}""", AntivirusProviderHealth.Good);
        var findings = SecurityRules.DefenderFindings(state).ToList();
        Assert.Contains(findings, f => f.Title == "Windows Security reports antivirus protection healthy");
        Assert.DoesNotContain(findings, f => f.Title == "Real-time protection is off" || f.Title.StartsWith("Protected by"));
    }

    [Fact]
    public void HealthyProviderStillKeepsActiveThreatWarnings()
    {
        var state = SecurityRules.ParseDefender("""{"Threats":["Test threat"],"OtherAv":["Product"]}""", AntivirusProviderHealth.Good);
        Assert.Contains(SecurityRules.DefenderFindings(state), f => f.Title == "Active threat: Test threat" && f.Severity == FindingSeverity.High);
    }

    [Fact]
    public void ScheduledTasksArePerUserAndNeverHaveAnInteractiveProcessDeadline()
    {
        const string userA = "S-1-5-21-1-2-3-1001", userB = "S-1-5-21-1-2-3-1002";
        Assert.NotEqual(ScheduledScanTask.TaskNameForUser(userA), ScheduledScanTask.TaskNameForUser(userB));
        Assert.Contains(userA, ScheduledScanTask.BuildDeleteArguments(userA));
        Assert.DoesNotContain(userB, ScheduledScanTask.BuildDeleteArguments(userA));
        Assert.Contains(userB, ScheduledScanTask.BuildCreateFromXmlArguments("job.xml", userB));
        var xml = ScheduledScanTask.BuildTaskXml(@"C:\Tools\Evict.exe", "Daily", 9, 1, userA);
        Assert.Contains("<AllowHardTerminate>false</AllowHardTerminate>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.True(ScheduledScanTask.LegacyTaskBelongsToUser(xml, userA));
        Assert.False(ScheduledScanTask.LegacyTaskBelongsToUser(xml, userB));
        Assert.False(ScheduledScanTask.LegacyTaskBelongsToUser(xml.Replace("--scheduled-scan", "--tray"), userA));
        Assert.False(ScheduledScanTask.LegacyTaskBelongsToUser("not xml", userA));
        Assert.Throws<ArgumentException>(() => ScheduledScanTask.BuildTaskXml("e.exe", "Daily", 9, 0, null));
        Assert.Throws<ArgumentException>(() => ScheduledScanTask.TaskNameForUser("\" /Delete /TN other"));
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Off")]
    public async Task LegacyMigrationNeverChangesAnotherUsersTask(string mode)
    {
        const string userA = "S-1-5-21-1-2-3-1001", userB = "S-1-5-21-1-2-3-1002";
        var commands = new List<string>();
        var xml = ScheduledScanTask.BuildTaskXml(@"C:\Tools\Evict.exe", "Daily", 9, 0, userB);
        var service = new ScheduledScanService(() => userA, (args, _) =>
        {
            commands.Add(args);
            return Task.FromResult(new ProcessResult { StdOut = xml });
        }, Path.Combine(_temp, "schedule.xml"), @"C:\Tools\Evict.exe");
        Assert.True((await service.MigrateLegacyAsync(mode, 10, 0, CancellationToken.None)).Ok);
        Assert.Single(commands);
        Assert.StartsWith("/Query", commands[0]);
        Assert.False(File.Exists(Path.Combine(_temp, "schedule.xml")));
    }

    [Theory]
    [InlineData("Daily")]
    [InlineData("Off")]
    public async Task LegacyMigrationUpdatesOnlyPositivelyOwnedTask(string mode)
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var commands = new List<string>();
        var xml = ScheduledScanTask.BuildTaskXml(@"C:\Tools\Evict.exe", "Daily", 9, 0, sid);
        var path = Path.Combine(_temp, "schedule.xml");
        var service = new ScheduledScanService(() => sid, (args, _) =>
        {
            commands.Add(args);
            return Task.FromResult(new ProcessResult
            {
                ExitCode = args.StartsWith("/Query") && args.Contains(sid) ? 1 : 0,
                StdOut = args.EndsWith("/XML") ? xml : ""
            });
        }, path, @"C:\Tools\Evict.exe");
        Assert.True((await service.MigrateLegacyAsync(mode, 10, 0, CancellationToken.None)).Ok);
        Assert.Contains($"/Delete /F /TN \"{ScheduledScanTask.LegacyTaskName}\"", commands);
        if (mode == "Off")
        {
            Assert.DoesNotContain(commands, a => a.StartsWith("/Create"));
            Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Contains(ScheduledScanTask.BuildCreateFromXmlArguments(path, sid), commands);
            Assert.Contains("<UserId>" + sid + "</UserId>", File.ReadAllText(path));
            Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", File.ReadAllText(path));
        }
    }

    [Fact]
    public async Task FailedMigrationCreationRetainsLegacyTaskAndReportsError()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var commands = new List<string>();
        var xml = ScheduledScanTask.BuildTaskXml(@"C:\Tools\Evict.exe", "Daily", 9, 0, sid);
        var service = new ScheduledScanService(() => sid, (args, _) =>
        {
            commands.Add(args);
            return Task.FromResult(args.StartsWith("/Create")
                ? new ProcessResult { ExitCode = 1, StdErr = "permission denied" }
                : new ProcessResult { StdOut = xml });
        }, Path.Combine(_temp, "schedule.xml"), @"C:\Tools\Evict.exe");
        var result = await service.MigrateLegacyAsync("Daily", 10, 0, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("permission denied", result.Error);
        Assert.DoesNotContain(commands, a => a.StartsWith("/Delete"));
    }

    [Fact]
    public async Task ConcurrentScheduleChangesDoNotOverwriteTheDefinitionBeingCreated()
    {
        const string sid = "S-1-5-21-1-2-3-1001";
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(_temp, "schedule.xml");
        var created = new List<string>();
        var service = new ScheduledScanService(() => sid, async (args, _) =>
        {
            if (!args.StartsWith("/Create")) return new ProcessResult { ExitCode = 1 };
            var xml = File.ReadAllText(path);
            created.Add(xml);
            if (created.Count == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(xml, File.ReadAllText(path));
            }
            return new ProcessResult();
        }, path, @"C:\Tools\Evict.exe");
        var first = service.ApplyAsync("Daily", 8, 0, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.ApplyAsync("Weekly", 9, 0, CancellationToken.None);
        Assert.Single(created);
        releaseFirst.SetResult();
        Assert.True((await first).Ok);
        Assert.True((await second).Ok);
        Assert.Equal(2, created.Count);
        Assert.Contains("<ScheduleByDay>", created[0]);
        Assert.Contains("<ScheduleByWeek>", created[1]);
    }

    private WindowsUpdatesService UpdateService(int? exitCode, List<WindowsUpdateInfo> remaining, string? error = null, Action<string>? inspectArguments = null) =>
        new(() => true, (_, args, _) => { inspectArguments?.Invoke(args); return Task.FromResult(exitCode); }, _ => Task.FromResult((remaining, error)));

    [Fact]
    public async Task UpdateRemovalUsesInteractiveSupportedArgumentsAndVerifiesAbsence()
    {
        var service = UpdateService(0, new(), inspectArguments: args => Assert.Equal("/uninstall /kb:123456", args));
        var result = await service.UninstallAsync(new WindowsUpdateInfo { HotFixId = "KB123456" }, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.False(result.RebootRequired);
        Assert.Contains("verified absent", result.Message);
    }

    [Fact]
    public async Task UpdateRemovalDoesNotClaimSuccessWhenUpdateStillPresentOrVerificationFails()
    {
        var update = new WindowsUpdateInfo { HotFixId = "KB123456" };
        Assert.False((await UpdateService(0, new() { update }).UninstallAsync(update, CancellationToken.None)).Ok);
        Assert.False((await UpdateService(0, new(), "inventory unavailable").UninstallAsync(update, CancellationToken.None)).Ok);
    }

    [Theory]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(1223, false, false)]
    [InlineData(2359303, false, false)]
    [InlineData(-2145124341, false, false)] // WU_E_CALL_CANCELLED
    public async Task UpdateRemovalReportsRestartAndCancellation(int code, bool ok, bool restart)
    {
        var result = await UpdateService(code, new()).UninstallAsync(new WindowsUpdateInfo { HotFixId = "KB123456" }, CancellationToken.None);
        Assert.Equal(ok, result.Ok);
        Assert.Equal(restart, result.RebootRequired);
    }

    [Fact]
    public async Task UpdateRemovalRejectsInvalidIdentifiersBeforeLaunchingAnything()
    {
        var called = false;
        var service = UpdateService(0, new(), inspectArguments: _ => called = true);
        Assert.False((await service.UninstallAsync(new WindowsUpdateInfo { HotFixId = "KB123456 /quiet" }, CancellationToken.None)).Ok);
        Assert.False(called);
    }

    private static ToggleItem ServiceItem(string name = "AcmeUpdater", bool awake = true, bool delayed = true) => new()
    {
        Id = "svc:" + name, Name = name, Group = "Services", IsOn = awake,
        Data = new Dictionary<string, string> { ["kind"] = "service", ["name"] = name, ["delayed"] = delayed ? "1" : "0" }
    };

    [Fact]
    public void HibernationPersistsOriginalStateBeforeCommandsAndClearsOnlyAfterWake()
    {
        var running = true;
        var calls = new List<string>();
        var service = new HibernationService(RecordFile, () => true, (_, args) =>
        {
            calls.Add(args);
            Assert.Equal("delayed-auto", HibernationService.LoadRecord(RecordFile).Services["acmeupdater"]);
            if (args.StartsWith("stop")) running = false;
            if (args.StartsWith("start")) running = true;
            return null;
        }, _ => running);
        var item = ServiceItem();
        Assert.Null(service.Set(item, false));
        Assert.False(item.IsOn);
        Assert.True(File.Exists(RecordFile));
        Assert.Null(service.Set(item, true));
        Assert.True(item.IsOn);
        Assert.Contains("config \"AcmeUpdater\" start= delayed-auto", calls);
        Assert.Empty(HibernationService.LoadRecord(RecordFile).Services);
    }

    [Fact]
    public void HibernationPersistenceFailurePreventsAllServiceMutation()
    {
        var calls = 0;
        var service = new HibernationService(RecordFile, () => true, (_, _) => { calls++; return null; }, _ => true,
            _ => throw new IOException("disk full"));
        var item = ServiceItem();
        Assert.Contains("disk full", service.Set(item, false));
        Assert.Equal(0, calls);
        Assert.True(item.IsOn);
    }

    [Fact]
    public void RepeatedSleepPreservesOriginalRunningStateForWake()
    {
        var running = true;
        var commands = new List<string>();
        var service = new HibernationService(RecordFile, () => true, (_, args) =>
        {
            commands.Add(args);
            if (args.StartsWith("stop")) running = false;
            if (args.StartsWith("start")) running = true;
            return null;
        }, _ => running);
        var item = ServiceItem();
        Assert.Null(service.Set(item, false));
        Assert.Null(service.Set(item, false));
        Assert.True(HibernationService.LoadRecord(RecordFile).ServiceWasRunning["AcmeUpdater"]);
        Assert.Null(service.Set(item, true));
        Assert.Contains("start \"AcmeUpdater\"", commands);
    }

    [Fact]
    public void FailedServiceStopRollsBackOriginalStartupState()
    {
        var calls = new List<string>();
        var service = new HibernationService(RecordFile, () => true, (_, args) =>
        {
            calls.Add(args);
            return args.StartsWith("stop") ? "cannot stop" : null;
        }, _ => true);
        Assert.Contains("cannot stop", service.Set(ServiceItem(), false));
        Assert.Contains("config \"AcmeUpdater\" start= delayed-auto", calls);
        Assert.Empty(HibernationService.LoadRecord(RecordFile).Services);
    }

    [Fact]
    public void FailedRollbackKeepsOriginalStateAvailableForLaterWake()
    {
        var fail = true;
        var service = new HibernationService(RecordFile, () => true, (_, args) =>
            fail && (args.StartsWith("stop") || args.EndsWith("delayed-auto")) ? "simulated command failure" : null, _ => true);
        var item = ServiceItem();
        Assert.Contains("Rollback failed", service.Set(item, false));
        Assert.Equal("delayed-auto", HibernationService.LoadRecord(RecordFile).Services[item.Name]);
        fail = false;
        Assert.Null(service.Set(item, true));
        Assert.Empty(HibernationService.LoadRecord(RecordFile).Services);
    }

    [Fact]
    public void CorruptRecoveryRecordFailsClosedWithoutCommandsOrReplacement()
    {
        File.WriteAllText(RecordFile, "not json");
        var calls = 0;
        var service = new HibernationService(RecordFile, () => true, (_, _) => { calls++; return null; }, _ => true);
        Assert.NotNull(service.Set(ServiceItem(), false));
        Assert.Equal(0, calls);
        Assert.Equal("not json", File.ReadAllText(RecordFile));
    }

    [Fact]
    public void FailedWakeRecordCleanupKeepsDurableRecoveryData()
    {
        var saves = 0;
        var service = new HibernationService(RecordFile, () => true, (_, _) => null, _ => false, r =>
        {
            if (++saves == 2) throw new IOException("write failed");
            HibernationService.SaveRecord(RecordFile, r);
        });
        var item = ServiceItem();
        Assert.Null(service.Set(item, false));
        Assert.Contains("record could not be cleared", service.Set(item, true));
        Assert.Equal("delayed-auto", HibernationService.LoadRecord(RecordFile).Services[item.Name]);
    }

    [Fact]
    public void TaskWakeRecordIsPersistedBeforeDisabling()
    {
        var item = new ToggleItem
        {
            Id = "task:Acme", Name = "Acme", Group = "Tasks", IsOn = true,
            Data = new Dictionary<string, string> { ["kind"] = "task", ["full"] = @"\Acme", ["admin"] = "0" }
        };
        var service = new HibernationService(RecordFile, () => true, (_, _) =>
        {
            Assert.Contains(@"\Acme", HibernationService.LoadRecord(RecordFile).Tasks);
            return null;
        }, _ => throw new Exception("No service lookup expected"));
        Assert.Null(service.Set(item, false));
        Assert.Null(service.Set(item, true));
        Assert.Empty(HibernationService.LoadRecord(RecordFile).Tasks);
    }

    [Fact]
    public async Task ConcurrentWakeAndSleepPreserveOtherRecoveryEntries()
    {
        HibernationService.SaveRecord(RecordFile, new HibernationRecord
        {
            Services = new() { ["First"] = "auto" }, ServiceWasRunning = new() { ["First"] = false }
        });
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondAttempted = new ManualResetEventSlim();
        var service = new HibernationService(RecordFile, () => true, (_, args) =>
        {
            if (args == "config \"First\" start= auto")
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            }
            return null;
        }, _ => false);
        var first = Task.Run(() => service.Set(ServiceItem("First", awake: false, delayed: false), true));
        Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => { secondAttempted.Set(); return service.Set(ServiceItem("Second", delayed: false), false); });
        Assert.True(secondAttempted.Wait(TimeSpan.FromSeconds(5)));
        releaseFirst.Set();
        Assert.Null(await first);
        Assert.Null(await second);
        var recovered = HibernationService.LoadRecord(RecordFile);
        Assert.DoesNotContain("First", recovered.Services.Keys);
        Assert.Equal("auto", recovered.Services["Second"]);
    }
}
