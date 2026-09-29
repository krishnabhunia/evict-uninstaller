using System.Text.RegularExpressions;
using Evict.Core.Models;
using Microsoft.Win32;

namespace Evict.Core.Services;

public enum NotificationSenderKind { Normal, Essential, Promotional }

/// <summary>Pure rules for notification senders (display names, what to keep, what to silence). Unit tested.</summary>
public static partial class NotificationRules
{
    private static readonly string[] EssentialPrefixes =
    {
        "Windows.SystemToast.SecurityAndMaintenance", "Windows.Defender", "Microsoft.Windows.SecHealthUI", "Microsoft.SecHealthUI",
        "Windows.SystemToast.WindowsUpdate", "Microsoft.Windows.SecurityCenter", "Windows.SystemToast.BdeUnlock", "Windows.SystemToast.BackgroundAccess",
        "Evict",
    };

    private static readonly string[] PromotionalPrefixes =
    {
        "Windows.SystemToast.Suggested", "Microsoft.Getstarted", "Microsoft.GetHelp", "Microsoft.MicrosoftOfficeHub",
        "Microsoft.XboxGamingOverlay", "Microsoft.GamingApp", "Microsoft.549981C3F5F10", "Microsoft.Windows.Cortana",
        "Microsoft.BingNews", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.People", "Microsoft.SkypeApp",
        "Microsoft.WindowsFeedbackHub", "Microsoft.MixedReality.Portal", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo",
    };

    /// <summary>Known folders that prefix unpackaged apps' notification IDs ("{GUID}\Vendor\app.exe").</summary>
    private static readonly Dictionary<string, Environment.SpecialFolder> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = Environment.SpecialFolder.ProgramFiles,
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = Environment.SpecialFolder.ProgramFilesX86,
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = Environment.SpecialFolder.Windows,
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = Environment.SpecialFolder.System,
    };

    [GeneratedRegex(@"_[a-z0-9]{13}(![\w.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageSuffix();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    public static NotificationSenderKind Classify(string id, IEnumerable<string> bloatwarePrefixes)
    {
        if (EssentialPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return NotificationSenderKind.Essential;
        if (PromotionalPrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return NotificationSenderKind.Promotional;
        if (bloatwarePrefixes.Any(p => p.Length > 3 && id.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return NotificationSenderKind.Promotional;
        return NotificationSenderKind.Normal;
    }

    /// <summary>
    /// Readable name for a notification sender ID: "Microsoft.WindowsStore_8wekyb3d8bbwe!App" → "Microsoft.WindowsStore",
    /// "Windows.SystemToast.SecurityAndMaintenance" → "Windows – Security And Maintenance",
    /// "{6D809377-…}\Zoom\bin\Zoom.exe" → "Zoom".
    /// </summary>
    public static string DisplayName(string id)
    {
        if (id.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase))
            return "Windows – " + CamelBoundary().Replace(id["Windows.SystemToast.".Length..], " ");
        if (id.Contains('\\') || id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var leaf = id.TrimEnd('\\').Split('\\').Last();
            return Path.GetFileNameWithoutExtension(leaf);
        }
        return PackageSuffix().Replace(id, "");
    }

    /// <summary>"{6D809377-…}\Zoom\bin\Zoom.exe" → "C:\Program Files\Zoom\bin\Zoom.exe" (null when the ID is not a path).</summary>
    public static string? ResolvePath(string id, Func<Environment.SpecialFolder, string> folderOf)
    {
        if (id.Length > 3 && id[1] == ':' && id[2] == '\\') return id;
        int slash = id.IndexOf('\\');
        if (slash <= 0 || !KnownFolders.TryGetValue(id[..slash], out var folder)) return null;
        var root = folderOf(folder);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, id[(slash + 1)..]);
    }
}

/// <summary>
/// Apps allowed to show Windows notifications (HKCU\…\Notifications\Settings\&lt;sender&gt;, value Enabled = 0 turns one off)
/// plus Windows' own tip / suggestion prompts. Everything is per user and reversible.
/// </summary>
public sealed class NotificationService : IToggleProvider
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    /// <summary>Windows' own promotional prompts: (id, name, key, value names, detail).</summary>
    private static readonly (string Id, string Name, string Key, string[] Values, string Detail)[] WindowsPrompts =
    {
        ("tips", "Tips and suggestions when using Windows", ContentDelivery, new[] { "SubscribedContent-338389Enabled" }, "Pop-ups with tips, tricks and suggestions."),
        ("welcome", "Windows welcome experience after updates", ContentDelivery, new[] { "SubscribedContent-310093Enabled" }, "Full-screen \"what's new\" pages after feature updates."),
        ("scoobe", "\"Finish setting up your device\" reminders", @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", new[] { "ScoobeSystemSettingEnabled" }, "Nag screens suggesting Microsoft 365, OneDrive backup, phone link…"),
        ("settings", "Suggested content in Settings", ContentDelivery, new[] { "SubscribedContent-338393Enabled", "SubscribedContent-353694Enabled", "SubscribedContent-353696Enabled" }, "Promotions shown inside the Settings app."),
    };

    public string Title => "Notifications";
    public string Subtitle => "Apps allowed to show Windows notifications, and Windows' own tips and suggestion pop-ups. Switching one off only silences it; it can be switched on again here or in Settings → System → Notifications.";
    public string OnLabel => "Allowed";

    public List<ToggleItem> Load()
    {
        var items = new List<ToggleItem>();
        var bloat = AppxService.KnownBloatware;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            foreach (var id in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var k = root!.OpenSubKey(id);
                if (k is null) continue;
                bool on = k.GetValue("Enabled") is not int v || v != 0;
                var kind = NotificationRules.Classify(id, bloat);
                var path = NotificationRules.ResolvePath(id, Environment.GetFolderPath);
                string name = NotificationRules.DisplayName(id);
                if (path != null && File.Exists(path))
                {
                    try { var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; if (!string.IsNullOrWhiteSpace(d)) name = d.Trim(); } catch { /* keep */ }
                }
                items.Add(new ToggleItem
                {
                    Id = id, Name = name, Group = kind == NotificationSenderKind.Essential ? "Security and system (kept on)" : "Apps",
                    Detail = kind == NotificationSenderKind.Promotional ? "Promotional / bundled app – usually safe to silence" : id,
                    IsOn = on, RecommendOff = kind == NotificationSenderKind.Promotional,
                    Locked = kind == NotificationSenderKind.Essential, LockedReason = kind == NotificationSenderKind.Essential ? "Security and update warnings stay on." : null,
                    Data = new Dictionary<string, string> { ["type"] = "sender" },
                });
            }
        }
        catch (Exception ex) { Log.Warn("Reading notification senders failed: " + ex.Message); }

        foreach (var (id, name, key, values, detail) in WindowsPrompts)
        {
            bool on = true;
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                // Windows treats a missing value as "on".
                on = values.Any(v => k?.GetValue(v) is not int n || n != 0);
            }
            catch { /* assume on */ }
            items.Add(new ToggleItem
            {
                Id = id, Name = name, Group = "Windows tips and suggestions", Detail = detail, IsOn = on, RecommendOff = true,
                Data = new Dictionary<string, string> { ["type"] = "prompt", ["key"] = key, ["values"] = string.Join("|", values) },
            });
        }
        return items;
    }

    public string? Set(ToggleItem item, bool on)
    {
        if (item.Locked) return item.LockedReason ?? "This item cannot be changed.";
        try
        {
            if (item.Data.TryGetValue("type", out var type) && type == "prompt")
            {
                using var k = Registry.CurrentUser.CreateSubKey(item.Data["key"], writable: true);
                foreach (var v in item.Data["values"].Split('|')) k.SetValue(v, on ? 1 : 0, RegistryValueKind.DWord);
            }
            else
            {
                using var k = Registry.CurrentUser.CreateSubKey($@"{SettingsKey}\{item.Id}", writable: true);
                if (on) k.DeleteValue("Enabled", throwOnMissingValue: false); // default = allowed
                else k.SetValue("Enabled", 0, RegistryValueKind.DWord);
            }
            item.IsOn = on;
            Log.Info($"Notifications: {item.Id} → {(on ? "on" : "off")}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
