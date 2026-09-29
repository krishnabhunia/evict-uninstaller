using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>
/// Lists and removes Microsoft Store / UWP (Appx / MSIX) packages via Windows PowerShell's Appx module.
/// Using PowerShell keeps us off the WinRT projection dependency and works on every Windows 10/11 build.
/// </summary>
public sealed class AppxService
{
    /// <summary>Package name fragments Microsoft pre-installs that most users consider bloat.</summary>
    public static readonly string[] KnownBloatware =
    {
        "Microsoft.3DBuilder", "Microsoft.Microsoft3DViewer", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance",
        "Microsoft.BingSports", "Microsoft.BingSearch", "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.Messaging",
        "Microsoft.MicrosoftOfficeHub", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MixedReality.Portal",
        "Microsoft.Office.OneNote", "Microsoft.OneConnect", "Microsoft.People", "Microsoft.Print3D", "Microsoft.SkypeApp",
        "Microsoft.Wallet", "Microsoft.WindowsAlarms", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps",
        "Microsoft.WindowsSoundRecorder", "Microsoft.Xbox.TCUI", "Microsoft.XboxApp", "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay",
        "Microsoft.YourPhone", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "Microsoft.GamingApp", "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop", "MicrosoftTeams", "MSTeams", "Microsoft.549981C3F5F10" /* Cortana */,
        "Clipchamp.Clipchamp", "Microsoft.OutlookForWindows", "Microsoft.Windows.DevHome", "Microsoft.MicrosoftStickyNotes",
        "Microsoft.WindowsCommunicationsApps" /* Mail & Calendar */, "Microsoft.BingTranslator", "Microsoft.NetworkSpeedTest",
        "king.com.CandyCrush", "king.com.BubbleWitch", "SpotifyAB.SpotifyMusic", "Disney.37853FC22B2CE", "Facebook.Facebook",
        "Facebook.InstagramBeta", "BytedancePte.Ltd.TikTok", "AmazonVideo.PrimeVideo", "Netflix", "Duolingo", "Fitbit",
        "Microsoft.Copilot", "Microsoft.Windows.Ai.Copilot.Provider", "Microsoft.MicrosoftJournal", "Microsoft.Whiteboard",
        "Microsoft.Advertising.Xaml",
    };

    private sealed class Row
    {
        public string? Name { get; set; }
        public string? PackageFullName { get; set; }
        public string? PackageFamilyName { get; set; }
        public string? Version { get; set; }
        public string? Publisher { get; set; }
        public string? InstallLocation { get; set; }
        public string? Architecture { get; set; }
        public bool IsFramework { get; set; }
        public bool IsBundle { get; set; }
        public bool NonRemovable { get; set; }
        public string? SignatureKind { get; set; }
    }

    public async Task<(List<AppxPackageInfo> Packages, string? Error)> GetPackagesAsync(bool allUsers, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        if (!PowerShellRunner.IsAvailable) return (new(), "Windows PowerShell was not found on this system.");
        progress?.Report(new ProgressReport("Querying Windows apps…", 10));

        var scopeArg = allUsers && ElevationHelper.IsElevated ? "-AllUsers" : "";
        var script = $$"""
            $pk = Get-AppxPackage {{scopeArg}} | ForEach-Object {
              [pscustomobject]@{
                Name = $_.Name; PackageFullName = $_.PackageFullName; PackageFamilyName = $_.PackageFamilyName;
                Version = [string]$_.Version; Publisher = $_.Publisher; InstallLocation = $_.InstallLocation;
                Architecture = [string]$_.Architecture; IsFramework = [bool]$_.IsFramework; IsBundle = [bool]$_.IsBundle;
                NonRemovable = [bool]$_.NonRemovable; SignatureKind = [string]$_.SignatureKind
              }
            }
            ConvertTo-Json -InputObject @($pk) -Depth 2 -Compress
            """;

        var (rows, error) = await PowerShellRunner.RunJsonAsync<List<Row>>(script, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (rows is null) return (new(), error ?? "No output from PowerShell.");

        progress?.Report(new ProgressReport("Reading app manifests…", 60));
        var list = new List<AppxPackageInfo>(rows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(r.Name) || string.IsNullOrEmpty(r.PackageFullName)) continue;
            if (!seen.Add(r.PackageFullName)) continue;
            var info = new AppxPackageInfo
            {
                Name = r.Name,
                PackageFullName = r.PackageFullName,
                PackageFamilyName = r.PackageFamilyName,
                Version = r.Version,
                Publisher = r.Publisher,
                InstallLocation = r.InstallLocation,
                Architecture = r.Architecture,
                IsFramework = r.IsFramework,
                IsBundle = r.IsBundle,
                NonRemovable = r.NonRemovable,
                SignatureKind = r.SignatureKind,
                IsKnownBloatware = KnownBloatware.Any(b => r.Name.StartsWith(b, StringComparison.OrdinalIgnoreCase)),
                PublisherDisplayName = PublisherOrganization(r.Publisher),
            };
            EnrichFromManifest(info);
            list.Add(info);
        }

        progress?.Report(new ProgressReport("Measuring sizes…", 80));
        await Parallel.ForEachAsync(list.Where(p => !string.IsNullOrEmpty(p.InstallLocation)), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, (p, token) =>
        {
            p.SizeBytes = DirectorySizeCalculator.Measure(p.InstallLocation, token);
            try { if (Directory.Exists(p.InstallLocation)) p.InstallDate = Directory.GetCreationTime(p.InstallLocation!); } catch { /* ignore */ }
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        progress?.Report(new ProgressReport("Done", 100));
        return (list.OrderBy(p => p.DisplayName ?? p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), error);
    }

    /// <summary>"CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond…" → "Microsoft Corporation".</summary>
    internal static string? PublisherOrganization(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return null;
        var m = Regex.Match(publisher, @"(?:^|,\s*)(?:O|CN)=([^,]+)");
        return m.Success ? m.Groups[1].Value.Trim() : publisher;
    }

    private static void EnrichFromManifest(AppxPackageInfo info)
    {
        if (string.IsNullOrEmpty(info.InstallLocation)) { info.DisplayName = FriendlyName(info.Name); return; }
        try
        {
            var manifest = Path.Combine(info.InstallLocation, "AppxManifest.xml");
            if (!File.Exists(manifest)) { info.DisplayName = FriendlyName(info.Name); return; }
            var doc = XDocument.Load(manifest);
            var props = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Properties");
            var displayName = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
            var publisherDisplay = props?.Elements().FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;
            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                info.DisplayName = displayName.Trim();
            else
                info.DisplayName = FriendlyName(info.Name);
            if (!string.IsNullOrWhiteSpace(publisherDisplay) && !publisherDisplay.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                info.PublisherDisplayName = publisherDisplay.Trim();
        }
        catch
        {
            info.DisplayName = FriendlyName(info.Name);
        }
    }

    /// <summary>"Microsoft.WindowsSoundRecorder" → "Windows Sound Recorder".</summary>
    internal static string FriendlyName(string packageName)
    {
        var s = packageName;
        int dot = s.IndexOf('.');
        if (dot > 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        s = s.Replace("Microsoft", "").Replace(".", " ").Trim();
        s = Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? packageName : s;
    }

    public async Task<(bool Ok, string Message)> RemoveAsync(AppxPackageInfo package, bool allUsers, CancellationToken ct)
    {
        if (package.NonRemovable) return (false, "Windows marks this package as non-removable.");
        var scope = allUsers && ElevationHelper.IsElevated ? "-AllUsers" : "";
        var script = $"Remove-AppxPackage -Package {PowerShellRunner.Quote(package.PackageFullName)} {scope} -ErrorAction Stop; 'OK'";
        var res = await PowerShellRunner.RunScriptAsync(script, ct, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        bool ok = res.StdOut.Contains("OK") && string.IsNullOrWhiteSpace(res.StdErr);
        var msg = ok ? "Removed." : FirstLine(res.StdErr) ?? FirstLine(res.StdOut) ?? $"PowerShell exited with code {res.ExitCode}.";
        Log.Info($"Remove-AppxPackage {package.PackageFullName}: {msg}");
        return (ok, msg);
    }

    /// <summary>Also removes the provisioned package so it does not come back for new user accounts.</summary>
    public async Task<(bool Ok, string Message)> RemoveProvisionedAsync(AppxPackageInfo package, CancellationToken ct)
    {
        if (!ElevationHelper.IsElevated) return (false, "Administrator rights required.");
        var script = $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -eq {PowerShellRunner.Quote(package.Name)} }} | Remove-AppxProvisionedPackage -Online -ErrorAction Stop | Out-Null; 'OK'";
        var res = await PowerShellRunner.RunScriptAsync(script, ct, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        bool ok = res.StdOut.Contains("OK") && string.IsNullOrWhiteSpace(res.StdErr);
        return (ok, ok ? "Provisioned package removed." : FirstLine(res.StdErr) ?? "Failed.");
    }

    private static string? FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var line = s.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line;
    }
}
