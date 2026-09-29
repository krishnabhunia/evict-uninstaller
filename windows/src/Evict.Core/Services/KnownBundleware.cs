using System.Text.Json;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>
/// Programs that are commonly reported as arriving as an "optional offer" inside other installers
/// (toolbars, browser hijackers, driver-updater trials, "PC optimizer" trials). Matching is by display
/// name fragment. Users can extend the list with %LocalAppData%\Evict\bundleware.json (a JSON array of strings).
/// </summary>
public static class KnownBundleware
{
    private static readonly string[] BuiltIn =
    {
        // Security "offers" that ride along with other installers
        "McAfee WebAdvisor", "McAfee Safe Connect", "McAfee Security Scan", "Norton Security Scan", "Norton Safe Web",
        "Avast Secure Browser", "AVG Secure Browser", "Avast SecureLine", "Segurazo", "ByteFence",
        // Toolbars / search hijackers
        "Search Protect", "Conduit", "Ask Toolbar", "Ask.com", "Yahoo Toolbar", "Bing Bar", "MyWay", "MyWebSearch", "Mindspark",
        "Babylon Toolbar", "Delta Toolbar", "SweetIM", "Sweet Packs", "Trovi", "Vosteran", "Yontoo", "Linkury", "Smartbar",
        "Iminent", "Wajam", "Snap.do", "Crossrider", "Funmoods", "Incredibar", "Softonic Toolbar", "Freeze.com",
        // Adware / shopping helpers
        "Shopper Pro", "PriceMeter", "DealPly", "Shopping Helper", "Coupon Printer", "SaveSense", "PriceFountain", "Web Companion",
        "OneLaunch", "Wave Browser", "WaveBrowser", "PC App Store", "Weather Nation", "Chromium-based Free Browser",
        // Driver / registry "updaters" and PC "optimizers" installed as trials
        "Driver Support", "DriverSupport", "Driver Update", "Driver Updater", "DriverUpdate", "WinZip Driver Updater",
        "WinZip Registry Optimizer", "WinZip System Utilities", "PC Accelerate", "PC Cleaner Pro", "PC Optimizer Pro",
        "Reimage Repair", "Restoro", "Smart PC Fixer", "Advanced System Protector", "RegClean Pro", "MyPC Backup",
        "Optimizer Pro", "SlimCleaner Plus", "PC HelpSoft", "Outbyte", "Auslogics Driver Updater", "System Mechanic Free",
        // Install-wrapper frameworks
        "InstallCore", "Amonetize", "OpenCandy", "InstallIQ", "Vittalia", "Somoto", "Solimba", "InstallBrain", "Tuto4PC",
    };

    private static List<string>? _cache;

    public static IReadOnlyList<string> Patterns
    {
        get
        {
            if (_cache != null) return _cache;
            var list = new List<string>(BuiltIn);
            try
            {
                var file = Path.Combine(AppPaths.DataRoot, "bundleware.json");
                if (File.Exists(file))
                {
                    var extra = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file));
                    if (extra != null) list.AddRange(extra.Where(s => !string.IsNullOrWhiteSpace(s)));
                }
            }
            catch (Exception ex) { Log.Warn("bundleware.json: " + ex.Message); }
            _cache = list;
            return list;
        }
    }

    /// <summary>Returns the matching pattern, or null.</summary>
    public static string? Match(string? displayName, string? publisher = null)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        foreach (var p in Patterns)
        {
            if (displayName.Contains(p, StringComparison.OrdinalIgnoreCase)) return p;
            if (publisher != null && p.Length >= 6 && publisher.Contains(p, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    public static void Apply(IEnumerable<InstalledProgram> programs)
    {
        foreach (var p in programs)
        {
            var m = Match(p.DisplayName, p.Publisher);
            if (m is null) continue;
            p.IsKnownBundleware = true;
            p.IsBundleSuspect = true;
            var note = $"Matches the known-bundleware list (\"{m}\") – commonly installed as an optional offer inside other downloads.";
            p.BundleGroupNote = string.IsNullOrEmpty(p.BundleGroupNote) ? note : note + " " + p.BundleGroupNote;
        }
    }
}
