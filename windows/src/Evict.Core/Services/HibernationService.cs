using System.Text.Json;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>A non-Microsoft scheduled task as reported by Get-ScheduledTask.</summary>
public sealed record ScheduledTaskInfo(string Path, string Name, bool Enabled, string? Execute, string? Author, string? Description);

/// <summary>Pure rules for Software Hibernation. Unit tested.</summary>
public static class HibernationRules
{
    private static readonly string[] UpdaterWords = { "update", "updater", "upgrade", "maintenance service", "autoupdate" };

    /// <summary>Components that must keep running: security, drivers / hardware panels, VPN, audio, backup and sync.</summary>
    private static readonly string[] ProtectedWords =
    {
        "antivirus", "anti-virus", "defender", "security", "firewall", "malware", "endpoint", "protect", "vpn", "tunnel",
        "nvidia", "amd ", "radeon", "intel", "realtek", "audio", "sound", "bluetooth", "driver", "display", "graphics",
        "touchpad", "synaptics", "elan", "backup", "sync", "onedrive", "dropbox", "google drive", "icloud", "evict",
        "printer", "print ", "scanner", "wacom", "logitech", "razer", "corsair", "steelseries", "battery", "power",
    };

    public static bool IsUpdater(params string?[] texts) =>
        texts.Any(t => !string.IsNullOrWhiteSpace(t) && UpdaterWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));

    public static bool IsProtected(params string?[] texts) =>
        texts.Any(t => !string.IsNullOrWhiteSpace(t) && ProtectedWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// A service that runs in the background without being asked: a Win32 service (own or shared process, not a driver),
    /// started automatically, whose program is outside Windows and not published by Microsoft.
    /// </summary>
    public static bool IsCandidateService(int type, int start, string? imagePath, string? company, string? windowsDir)
    {
        bool win32 = (type & 0x10) != 0 || (type & 0x20) != 0;
        if (!win32 || start != 2) return false;
        if (string.IsNullOrWhiteSpace(imagePath)) return false;
        if (!string.IsNullOrEmpty(windowsDir) && PathUtil.IsUnder(imagePath, windowsDir)) return false;
        if (company != null && company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>sc.exe start= argument that puts a service back the way it was.</summary>
    public static string ScStartArgument(bool delayed) => delayed ? "delayed-auto" : "auto";

    /// <summary>Parses Get-ScheduledTask JSON (object or array) into tasks, dropping Microsoft's own folder.</summary>
    public static List<ScheduledTaskInfo> ParseTasks(string? json)
    {
        var list = new List<ScheduledTaskInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var e in items)
            {
                string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var path = S("TaskPath") ?? "\\";
                var name = S("TaskName");
                if (name is null || path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
                bool enabled = !string.Equals(S("State"), "Disabled", StringComparison.OrdinalIgnoreCase);
                list.Add(new ScheduledTaskInfo(path, name, enabled, S("Execute"), S("Author"), S("Description")));
            }
        }
        catch (JsonException) { /* return what we have */ }
        return list;
    }
}

/// <summary>What Evict put to sleep, so it can always be woken (%LocalAppData%\Evict\hibernation.json).</summary>
public sealed class HibernationRecord
{
    /// <summary>Service name → start type it had ("auto" / "delayed-auto").</summary>
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Full task paths ("\GoogleUpdateTaskMachineUA") Evict disabled.</summary>
    public HashSet<string> Tasks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Software Hibernation: third-party background services and scheduled tasks (updaters, helpers) can be put to sleep –
/// services are set to start on demand and stopped, tasks are disabled – and woken again. Security, hardware, VPN,
/// audio, backup and sync components are never touched.
/// </summary>
public sealed class HibernationService : IToggleProvider
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private static string RecordFile => Path.Combine(AppPaths.DataRoot, "hibernation.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string Title => "Software hibernation";
    public string Subtitle => "Background services and scheduled tasks of your programs (updaters, helpers) that run all the time. Put one to sleep and it stops starting by itself; wake it here any time. A sleeping updater cannot update its program – use Software Updater instead. Security, driver, VPN, audio, backup and sync components are never put to sleep. Needs administrator rights.";
    public string OnLabel => "Awake";

    public static HibernationRecord LoadRecord()
    {
        try
        {
            if (File.Exists(RecordFile)) return JsonSerializer.Deserialize<HibernationRecord>(File.ReadAllText(RecordFile)) ?? new();
        }
        catch (Exception ex) { Log.Warn("Reading hibernation record failed: " + ex.Message); }
        return new HibernationRecord();
    }

    private static void SaveRecord(HibernationRecord r)
    {
        try { File.WriteAllText(RecordFile, JsonSerializer.Serialize(r, JsonOpts)); }
        catch (Exception ex) { Log.Warn("Saving hibernation record failed: " + ex.Message); }
    }

    public List<ToggleItem> Load()
    {
        var record = LoadRecord();
        var items = new List<ToggleItem>();
        bool admin = ElevationHelper.IsElevated;
        items.AddRange(LoadServices(record, admin));
        items.AddRange(LoadTasks(record, admin));
        return items;
    }

    private static IEnumerable<ToggleItem> LoadServices(HibernationRecord record, bool admin)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        using var root = Registry.LocalMachine.OpenSubKey(ServicesKey);
        if (root is null) yield break;
        foreach (var name in root.GetSubKeyNames())
        {
            ToggleItem? item = null;
            try
            {
                using var k = root.OpenSubKey(name);
                if (k is null) continue;
                int type = k.GetValue("Type") is int t ? t : 0;
                int start = k.GetValue("Start") is int s ? s : 0;
                bool sleeping = record.Services.ContainsKey(name);
                var image = UninstallCommandParser.Parse(k.GetValue("ImagePath") as string)?.FileName;
                string? company = null, description = null;
                if (image != null && File.Exists(image))
                {
                    try { var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(image); company = vi.CompanyName; description = vi.FileDescription; } catch { /* no version info */ }
                }
                // Our own sleeping services are listed even though they are now "manual".
                if (!sleeping && !HibernationRules.IsCandidateService(type, start, image, company, windows)) continue;
                var display = ResolveDisplayName(k.GetValue("DisplayName") as string) ?? name;
                var desc = ResolveDisplayName(k.GetValue("Description") as string);
                bool isProtected = HibernationRules.IsProtected(name, display, desc, company, description);
                bool updater = HibernationRules.IsUpdater(name, display, desc, description);
                item = new ToggleItem
                {
                    Id = "svc:" + name, Name = display, Group = "Background services",
                    Detail = $"{(company is { Length: > 0 } ? company + " · " : "")}{(updater ? "updater · " : "")}{image}",
                    IsOn = !sleeping,
                    RecommendOff = updater && !isProtected,
                    Locked = (isProtected && !sleeping) || !admin,
                    LockedReason = isProtected && !sleeping ? "Security, hardware, VPN, audio, backup and sync components stay awake." : !admin ? "Restart Evict as administrator to change services." : null,
                    Data = new Dictionary<string, string>
                    {
                        ["kind"] = "service", ["name"] = name,
                        ["delayed"] = k.GetValue("DelayedAutostart") is int d && d == 1 ? "1" : "0",
                    },
                };
            }
            catch { /* unreadable service key */ }
            if (item != null) yield return item;
        }
    }

    /// <summary>Service display names may be resource references ("@%SystemRoot%\x.dll,-101"); keep plain ones only.</summary>
    private static string? ResolveDisplayName(string? s) => string.IsNullOrWhiteSpace(s) || s.StartsWith('@') ? null : s;

    private static IEnumerable<ToggleItem> LoadTasks(HibernationRecord record, bool admin)
    {
        const string script = "Get-ScheduledTask | Where-Object { $_.TaskPath -notlike '\\Microsoft\\*' } | ForEach-Object { [pscustomobject]@{ TaskPath=$_.TaskPath; TaskName=$_.TaskName; State=[string]$_.State; Author=$_.Author; Description=$_.Description; Execute=($_.Actions | Select-Object -First 1).Execute } } | ConvertTo-Json -Compress";
        List<ScheduledTaskInfo> tasks;
        try
        {
            var res = PowerShellRunner.RunScriptAsync(script, CancellationToken.None, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
            tasks = HibernationRules.ParseTasks(res.StdOut);
        }
        catch (Exception ex) { Log.Warn("Listing scheduled tasks failed: " + ex.Message); yield break; }
        foreach (var t in tasks)
        {
            var full = t.Path.TrimEnd('\\') + "\\" + t.Name;
            bool sleeping = record.Tasks.Contains(full);
            if (!t.Enabled && !sleeping) continue; // disabled by someone else – not ours to wake
            bool isProtected = HibernationRules.IsProtected(t.Name, t.Author, t.Description, t.Execute);
            bool updater = HibernationRules.IsUpdater(t.Name, t.Description, t.Execute);
            bool machineTask = !string.IsNullOrEmpty(t.Execute) && (t.Execute.Contains("Program Files", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Machine", StringComparison.OrdinalIgnoreCase));
            yield return new ToggleItem
            {
                Id = "task:" + full, Name = t.Name, Group = "Scheduled tasks",
                Detail = $"{(t.Author is { Length: > 0 } ? t.Author + " · " : "")}{(updater ? "updater · " : "")}{t.Execute}",
                IsOn = !sleeping,
                RecommendOff = updater && !isProtected,
                Locked = isProtected && !sleeping,
                LockedReason = isProtected && !sleeping ? "Security, hardware, VPN, audio, backup and sync tasks stay awake." : null,
                Data = new Dictionary<string, string> { ["kind"] = "task", ["path"] = t.Path, ["name"] = t.Name, ["full"] = full, ["admin"] = machineTask ? "1" : "0" },
            };
        }
    }

    public string? Set(ToggleItem item, bool on)
    {
        if (item.Locked) return item.LockedReason ?? "This item cannot be changed.";
        var record = LoadRecord();
        try
        {
            string? error = item.Data["kind"] == "service" ? SetService(item, on, record) : SetTask(item, on, record);
            if (error != null) return error;
            SaveRecord(record);
            item.IsOn = on;
            Log.Info($"Hibernation: {item.Id} → {(on ? "awake" : "asleep")}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? SetService(ToggleItem item, bool on, HibernationRecord record)
    {
        if (!ElevationHelper.IsElevated) return "Restart Evict as administrator to change services.";
        var name = item.Data["name"];
        var sc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe");
        if (on)
        {
            var original = record.Services.TryGetValue(name, out var o) ? o : HibernationRules.ScStartArgument(item.Data["delayed"] == "1");
            var r = Run(sc, $"config \"{name}\" start= {original}");
            if (r != null) return r;
            Run(sc, $"start \"{name}\""); // best effort – some services start only when needed
            record.Services.Remove(name);
        }
        else
        {
            var r = Run(sc, $"config \"{name}\" start= demand");
            if (r != null) return r;
            Run(sc, $"stop \"{name}\"");
            record.Services[name] = HibernationRules.ScStartArgument(item.Data["delayed"] == "1");
        }
        return null;
    }

    private static string? SetTask(ToggleItem item, bool on, HibernationRecord record)
    {
        var full = item.Data["full"];
        var schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        var r = Run(schtasks, $"/Change /TN \"{full.TrimStart('\\')}\" {(on ? "/ENABLE" : "/DISABLE")}");
        if (r != null) return item.Data["admin"] == "1" && !ElevationHelper.IsElevated ? "Restart Evict as administrator to change this task. (" + r + ")" : r;
        if (on) record.Tasks.Remove(full); else record.Tasks.Add(full);
        return null;
    }

    private static string? Run(string exe, string args)
    {
        var res = ProcessRunner.RunCapturedAsync(exe, args, CancellationToken.None, TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (res.ExitCode == 0) return null;
        var text = (res.StdErr + " " + res.StdOut).Trim();
        return $"{PathUtil.LeafName(exe)} returned {res.ExitCode}{(text.Length > 0 ? ": " + text : "")}";
    }
}
