using Evict.Core.Models;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>Pure rules for Windows privacy permissions (ConsentStore). Unit tested.</summary>
public static class PermissionRules
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["webcam"] = "Camera", ["microphone"] = "Microphone", ["location"] = "Location", ["contacts"] = "Contacts",
        ["appointments"] = "Calendar", ["phoneCall"] = "Phone calls", ["phoneCallHistory"] = "Call history", ["email"] = "Email",
        ["chat"] = "Messaging", ["userAccountInformation"] = "Account info", ["radios"] = "Radios", ["bluetoothSync"] = "Other devices",
        ["documentsLibrary"] = "Documents", ["picturesLibrary"] = "Pictures", ["videosLibrary"] = "Videos", ["musicLibrary"] = "Music",
        ["downloadsFolder"] = "Downloads", ["broadFileSystemAccess"] = "File system", ["userNotificationListener"] = "Notifications",
        ["userDataTasks"] = "Tasks", ["cellularData"] = "Cellular data", ["activity"] = "Motion", ["humanPresence"] = "Presence sensing",
        ["gazeInput"] = "Eye tracking", ["graphicsCaptureProgrammatic"] = "Screenshots", ["graphicsCaptureWithoutBorder"] = "Screenshot borders",
        ["wifiData"] = "Wi-Fi data", ["sensors.custom"] = "Custom sensors", ["serialCommunication"] = "Serial devices", ["usb"] = "USB devices",
    };

    /// <summary>Permissions whose silent use would matter: an app holding one it never uses is worth switching off.</summary>
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "webcam", "microphone", "location", "contacts", "appointments", "phoneCall", "phoneCallHistory", "email", "chat",
        "userAccountInformation", "broadFileSystemAccess", "userNotificationListener", "graphicsCaptureProgrammatic",
    };

    public static string CapabilityName(string capability) => Names.TryGetValue(capability, out var n) ? n : capability;
    public static bool IsSensitive(string capability) => Sensitive.Contains(capability);

    /// <summary>Desktop apps are stored as their path with '#' for '\': "C:#Program Files#Zoom#bin#Zoom.exe".</summary>
    public static string NonPackagedPath(string keyName) => keyName.Replace('#', '\\');

    /// <summary>LastUsedTimeStart / Stop are FILETIMEs; 0 = never used.</summary>
    public static DateTime? FromFileTime(long value)
    {
        if (value <= 0) return null;
        try { return DateTime.FromFileTimeUtc(value).ToLocalTime(); } catch { return null; }
    }

    /// <summary>"Allow" / "Deny" (case-insensitive); anything else counts as allowed (Windows' default prompt state).</summary>
    public static bool IsAllowed(string? value) => !string.Equals(value, "Deny", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Which apps may use the camera, microphone, location, contacts… (HKCU\…\CapabilityAccessManager\ConsentStore).
/// Store apps are switched one by one; desktop apps only as a group per permission (Windows has no per-app switch for
/// them), so they are listed with their last use for information.
/// </summary>
public sealed class PermissionService : IToggleProvider
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public string Title => "Software permissions";
    public string Subtitle => "Apps allowed to use your camera, microphone, location, contacts and other private data – with when they last used it. Store apps are switched one by one; Windows lets desktop programs be allowed or blocked only all together, per permission.";
    public string OnLabel => "Allowed";

    public List<ToggleItem> Load()
    {
        var items = new List<ToggleItem>();
        using var root = Registry.CurrentUser.OpenSubKey(Store);
        if (root is null) return items;
        foreach (var cap in root.GetSubKeyNames())
        {
            using var ck = root.OpenSubKey(cap);
            if (ck is null) continue;
            var group = PermissionRules.CapabilityName(cap);
            bool masterOn = PermissionRules.IsAllowed(ck.GetValue("Value") as string);
            foreach (var app in ck.GetSubKeyNames())
            {
                if (app.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                using var ak = ck.OpenSubKey(app);
                if (ak is null) continue;
                var value = ak.GetValue("Value") as string;
                if (value is null) continue; // never asked – nothing granted
                var lastUsed = PermissionRules.FromFileTime(ak.GetValue("LastUsedTimeStop") is long l ? l : 0);
                bool on = PermissionRules.IsAllowed(value);
                items.Add(new ToggleItem
                {
                    Id = $"{cap}\\{app}", Name = NotificationRules.DisplayName(app), Group = group,
                    Detail = (lastUsed is { } d ? $"Last used {d:dd MMM yyyy}" : "Never used") + (masterOn ? "" : " · blocked for all apps by Windows settings"),
                    IsOn = on, RecommendOff = on && lastUsed is null && PermissionRules.IsSensitive(cap),
                    Data = new Dictionary<string, string> { ["key"] = $@"{Store}\{cap}\{app}" },
                });
            }

            using var np = ck.OpenSubKey("NonPackaged");
            if (np is null) continue;
            items.Add(new ToggleItem
            {
                Id = $"{cap}\\NonPackaged", Name = "Desktop programs (all)", Group = group,
                Detail = $"The single switch Windows has for every desktop program's {group.ToLowerInvariant()} access.",
                IsOn = PermissionRules.IsAllowed(np.GetValue("Value") as string),
                Data = new Dictionary<string, string> { ["key"] = $@"{Store}\{cap}\NonPackaged" },
            });
            foreach (var exeKey in np.GetSubKeyNames())
            {
                using var ek = np.OpenSubKey(exeKey);
                if (ek is null) continue;
                var path = PermissionRules.NonPackagedPath(exeKey);
                var lastUsed = PermissionRules.FromFileTime(ek.GetValue("LastUsedTimeStop") is long l ? l : 0);
                bool exists = File.Exists(path);
                string name = Path.GetFileNameWithoutExtension(path);
                if (exists)
                {
                    try { var desc = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim(); } catch { /* keep */ }
                }
                items.Add(new ToggleItem
                {
                    Id = $"{cap}\\NonPackaged\\{exeKey}", Name = name, Group = group,
                    Detail = (lastUsed is { } d ? $"Last used {d:dd MMM yyyy}" : "Never used") + " · " + (exists ? path : "program no longer installed: " + path),
                    IsOn = PermissionRules.IsAllowed(np.GetValue("Value") as string),
                    Locked = true, LockedReason = "Windows allows or blocks desktop programs only all together – use \"Desktop programs (all)\".",
                });
            }
        }
        return items;
    }

    public string? Set(ToggleItem item, bool on)
    {
        if (item.Locked) return item.LockedReason ?? "This item cannot be changed.";
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(item.Data["key"], writable: true);
            k.SetValue("Value", on ? "Allow" : "Deny", RegistryValueKind.String);
            item.IsOn = on;
            Log.Info($"Permission {item.Id} → {(on ? "Allow" : "Deny")}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
