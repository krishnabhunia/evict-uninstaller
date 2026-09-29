using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Flags programs that were installed within a few minutes of another program from a *different*
/// publisher – the classic signature of bundled "offers" riding along with a download.
/// Uses the registry key's last-write time, which is precise to the second.
/// </summary>
public static class BundlewareDetector
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(4);

    /// <summary>Publishers whose installers legitimately drop several entries at once.</summary>
    private static readonly HashSet<string> TrustedPublisherKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "intel", "nvidia", "amd", "advancedmicrodevices", "realtek", "dell", "hp", "hpinc", "lenovo",
        "asus", "asustek", "acer", "google", "apple", "adobe", "oracle", "logitech", "synaptics", "qualcomm",
        "mozilla", "python", "pythonsoftwarefoundation", "pythonfoundation", "jetbrains", "docker", "autodesk", "valve", "steam",
        "adobeincorporated", "oracleamerica", "logitechinc", "samsung", "samsungelectronics", "canon", "epson", "brother",
    };

    public static void Apply(List<InstalledProgram> programs)
    {
        var candidates = programs
            .Where(p => p.RegistryKeyLastWrite is not null && !p.IsSystemComponent)
            .OrderBy(p => p.RegistryKeyLastWrite)
            .ToList();

        for (int i = 0; i < candidates.Count; i++)
        {
            var a = candidates[i];
            var aPub = NameNormalizer.PublisherKey(a.Publisher);
            for (int j = i + 1; j < candidates.Count; j++)
            {
                var b = candidates[j];
                var delta = b.RegistryKeyLastWrite!.Value - a.RegistryKeyLastWrite!.Value;
                if (delta > Window) break;

                var bPub = NameNormalizer.PublisherKey(b.Publisher);
                if (aPub.Length > 0 && aPub == bPub) continue;                      // same vendor suite
                if (TrustedPublisherKeys.Contains(aPub) && TrustedPublisherKeys.Contains(bPub)) continue;
                if (SharesNameToken(a, b)) continue;                                  // "Foo" + "Foo Helper"

                // Only flag the *unknown* side: if one publisher is trusted, the other is the suspect.
                if (!TrustedPublisherKeys.Contains(aPub)) Mark(a, b);
                if (!TrustedPublisherKeys.Contains(bPub)) Mark(b, a);
            }
        }
    }

    private static bool SharesNameToken(InstalledProgram a, InstalledProgram b)
    {
        var ta = NameNormalizer.Tokens(a.DisplayName).Where(t => t.Length >= 4).ToHashSet();
        var tb = NameNormalizer.Tokens(b.DisplayName).Where(t => t.Length >= 4);
        return tb.Any(ta.Contains);
    }

    private static void Mark(InstalledProgram suspect, InstalledProgram companion)
    {
        suspect.IsBundleSuspect = true;
        var note = $"Installed within {Window.TotalMinutes:0} min of \"{companion.DisplayName}\"";
        if (string.IsNullOrEmpty(suspect.BundleGroupNote)) suspect.BundleGroupNote = note;
        else if (!suspect.BundleGroupNote.Contains(companion.DisplayName)) suspect.BundleGroupNote += "; " + companion.DisplayName;
    }
}
