using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

public enum StartupLocation
{
    UserRun, UserRunOnce, MachineRun, MachineRun32, MachineRunOnce, UserStartupFolder, CommonStartupFolder,
}

public sealed class StartupItem
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public required StartupLocation Location { get; init; }
    public string? ExePath { get; init; }
    public bool Enabled { get; init; } = true;
    public bool TargetExists { get; init; } = true;
    /// <summary>For folder items: the .lnk / file path. For registry items: the value's key path.</summary>
    public required string Source { get; init; }
    public bool RequiresAdmin => Location is StartupLocation.MachineRun or StartupLocation.MachineRun32 or StartupLocation.MachineRunOnce or StartupLocation.CommonStartupFolder;

    public string LocationText => Location switch
    {
        StartupLocation.UserRun => "Registry (current user)",
        StartupLocation.UserRunOnce => "Registry RunOnce (current user)",
        StartupLocation.MachineRun => "Registry (all users)",
        StartupLocation.MachineRun32 => "Registry (all users, 32-bit)",
        StartupLocation.MachineRunOnce => "Registry RunOnce (all users)",
        StartupLocation.UserStartupFolder => "Startup folder (current user)",
        StartupLocation.CommonStartupFolder => "Startup folder (all users)",
        _ => Location.ToString(),
    };
}

/// <summary>
/// Startup Apps: Run / RunOnce registry keys and the Startup folders, with the same enable/disable
/// switch Task Manager uses (Explorer's StartupApproved records).
/// </summary>
public sealed class StartupService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public List<StartupItem> GetItems()
    {
        var items = new List<StartupItem>();
        ReadRun(items, RegistryHive.CurrentUser, RegistryView.Registry64, RunKey, StartupLocation.UserRun, "Run");
        ReadRun(items, RegistryHive.CurrentUser, RegistryView.Registry64, RunOnceKey, StartupLocation.UserRunOnce, null);
        ReadRun(items, RegistryHive.LocalMachine, RegistryView.Registry64, RunKey, StartupLocation.MachineRun, "Run");
        ReadRun(items, RegistryHive.LocalMachine, RegistryView.Registry32, RunKey, StartupLocation.MachineRun32, "Run32");
        ReadRun(items, RegistryHive.LocalMachine, RegistryView.Registry64, RunOnceKey, StartupLocation.MachineRunOnce, null);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupLocation.UserStartupFolder, RegistryHive.CurrentUser);
        ReadFolder(items, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupLocation.CommonStartupFolder, RegistryHive.LocalMachine);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static void ReadRun(List<StartupItem> items, RegistryHive hive, RegistryView view, string subKey, StartupLocation loc, string? approvedSub)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey(subKey);
            if (key is null) return;
            var approved = approvedSub != null ? ReadApproved(hive, approvedSub) : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0) continue;
                var cmd = key.GetValue(name) as string ?? "";
                var parsed = UninstallCommandParser.Parse(cmd);
                var exe = parsed?.FileName;
                bool exists = exe is null || !Path.IsPathRooted(exe) || File.Exists(exe);
                items.Add(new StartupItem
                {
                    Name = name, Command = cmd, Location = loc, ExePath = exe, TargetExists = exists,
                    Enabled = !approved.TryGetValue(name, out var en) || en,
                    Source = RegistryPaths.Display(hive, view, subKey, name),
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Startup {loc}: {ex.Message}"); }
    }

    private static void ReadFolder(List<StartupItem> items, string folder, StartupLocation loc, RegistryHive approvedHive)
    {
        try
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            var approved = ReadApproved(approvedHive, "StartupFolder");
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(name), Command = file, Location = loc, ExePath = file, TargetExists = true,
                    Enabled = !approved.TryGetValue(name, out var en) || en,
                    Source = file,
                });
            }
        }
        catch (Exception ex) { Log.Warn($"Startup folder {loc}: {ex.Message}"); }
    }

    /// <summary>StartupApproved values: byte 0 = 0x02 enabled, 0x03 (or other odd values) disabled.</summary>
    private static Dictionary<string, bool> ReadApproved(RegistryHive hive, string sub)
    {
        var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryPaths.Join(ApprovedRoot, sub));
            if (key is null) return d;
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] b && b.Length >= 1) d[name] = IsEnabledFlag(b[0]);
            }
        }
        catch { /* ignore */ }
        return d;
    }

    internal static bool IsEnabledFlag(byte b) => (b & 1) == 0; // 0x02, 0x06 = enabled; 0x03, 0x07 = disabled

    internal static byte[] BuildApprovedValue(bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        return data;
    }

    private static (RegistryHive Hive, string Sub, string ValueName)? ApprovedTarget(StartupItem item) => item.Location switch
    {
        StartupLocation.UserRun => (RegistryHive.CurrentUser, "Run", item.Name),
        StartupLocation.MachineRun => (RegistryHive.LocalMachine, "Run", item.Name),
        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, "Run32", item.Name),
        StartupLocation.UserStartupFolder => (RegistryHive.CurrentUser, "StartupFolder", Path.GetFileName(item.Source)),
        StartupLocation.CommonStartupFolder => (RegistryHive.LocalMachine, "StartupFolder", Path.GetFileName(item.Source)),
        _ => null,
    };

    public (bool Ok, string? Error) SetEnabled(StartupItem item, bool enabled)
    {
        var target = ApprovedTarget(item);
        if (target is null) return (false, "RunOnce entries cannot be disabled – delete them instead.");
        if (item.RequiresAdmin && !ElevationHelper.IsElevated) return (false, "Administrator rights are required for all-users startup items.");
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(target.Value.Hive, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(RegistryPaths.Join(ApprovedRoot, target.Value.Sub), writable: true);
            key.SetValue(target.Value.ValueName, BuildApprovedValue(enabled), RegistryValueKind.Binary);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public (bool Ok, string? Error) Delete(StartupItem item)
    {
        if (item.RequiresAdmin && !ElevationHelper.IsElevated) return (false, "Administrator rights are required for all-users startup items.");
        try
        {
            switch (item.Location)
            {
                case StartupLocation.UserStartupFolder:
                case StartupLocation.CommonStartupFolder:
                    if (File.Exists(item.Source)) File.Delete(item.Source);
                    break;
                default:
                {
                    var (hive, view, sub) = item.Location switch
                    {
                        StartupLocation.UserRun => (RegistryHive.CurrentUser, RegistryView.Registry64, RunKey),
                        StartupLocation.UserRunOnce => (RegistryHive.CurrentUser, RegistryView.Registry64, RunOnceKey),
                        StartupLocation.MachineRun => (RegistryHive.LocalMachine, RegistryView.Registry64, RunKey),
                        StartupLocation.MachineRun32 => (RegistryHive.LocalMachine, RegistryView.Registry32, RunKey),
                        _ => (RegistryHive.LocalMachine, RegistryView.Registry64, RunOnceKey),
                    };
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(sub, writable: true);
                    key?.DeleteValue(item.Name, throwOnMissingValue: false);
                    break;
                }
            }
            // Remove the approval record too, so Task Manager does not show a ghost entry.
            var t = ApprovedTarget(item);
            if (t != null)
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(t.Value.Hive, RegistryView.Registry64);
                    using var key = baseKey.OpenSubKey(RegistryPaths.Join(ApprovedRoot, t.Value.Sub), writable: true);
                    key?.DeleteValue(t.Value.ValueName, throwOnMissingValue: false);
                }
                catch { /* ignore */ }
            }
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
