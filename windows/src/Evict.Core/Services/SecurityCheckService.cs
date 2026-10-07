using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Runtime.InteropServices;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

public enum FindingSeverity { High, Medium, Info }

/// <summary>Documented WSC_SECURITY_PROVIDER_HEALTH values, not the undocumented WMI productState bit field.</summary>
public enum AntivirusProviderHealth { Good, NotMonitored, Poor, Snooze }

/// <summary>One line of the "Malicious software &amp; extensions" check.</summary>
public sealed record SecurityFinding(string Section, string Title, string Detail, FindingSeverity Severity, string? ActionKey = null);

/// <summary>Microsoft Defender's state as reported by Get-MpComputerStatus / Get-MpThreat.</summary>
public sealed class DefenderStatus
{
    public bool? AntivirusEnabled { get; init; }
    public bool? RealTimeProtection { get; init; }
    public int? SignatureAgeDays { get; init; }
    public int? QuickScanAgeDays { get; init; }
    public string? Mode { get; init; }
    public List<string> ActiveThreats { get; init; } = new();
    public List<string> OtherAntivirus { get; init; } = new();
    public AntivirusProviderHealth? ProviderHealth { get; init; }
    public string? Error { get; init; }
}

/// <summary>Pure rules behind the security check. Unit tested.</summary>
public static class SecurityRules
{
    /// <summary>Parses the JSON the Defender script prints (see <see cref="SecurityCheckService"/>).</summary>
    public static DefenderStatus ParseDefender(string? json, AntivirusProviderHealth? providerHealth = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DefenderStatus { Error = "Microsoft Defender did not answer.", ProviderHealth = providerHealth };
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var r = doc.RootElement;
            bool? B(string n) => r.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            int? I(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
            string? S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            List<string> L(string n)
            {
                if (!r.TryGetProperty(n, out var v)) return new List<string>();
                if (v.ValueKind == JsonValueKind.String) return new List<string> { v.GetString()! };
                return v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
            }
            return new DefenderStatus
            {
                AntivirusEnabled = B("AntivirusEnabled"), RealTimeProtection = B("RealTime"), SignatureAgeDays = I("SigAge"),
                QuickScanAgeDays = I("QuickScanAge"), Mode = S("Mode"), ActiveThreats = L("Threats"), OtherAntivirus = L("OtherAv")
                    .Where(n => !IsMicrosoftDefender(n)).ToList(),
                Error = S("Error"),
                ProviderHealth = providerHealth,
            };
        }
        catch (JsonException) { return new DefenderStatus { Error = "Unreadable answer from Microsoft Defender.", ProviderHealth = providerHealth }; }
    }

    /// <summary>"Windows Defender" / "Microsoft Defender Antivirus" – not "Bitdefender".</summary>
    public static bool IsMicrosoftDefender(string productName)
    {
        var n = productName.Trim();
        return n.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("Microsoft Defender", StringComparison.OrdinalIgnoreCase)
               || n.StartsWith("Windows Defender ", StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<SecurityFinding> DefenderFindings(DefenderStatus d)
    {
        const string section = "Antivirus";
        foreach (var t in d.ActiveThreats)
            yield return new SecurityFinding(section, "Active threat: " + t, "Microsoft Defender found this and has not removed it yet. Open Windows Security to remove it.", FindingSeverity.High, "windowsSecurity");
        if (d.OtherAntivirus.Count > 0)
            yield return new SecurityFinding(section, "Registered antivirus: " + string.Join(", ", d.OtherAntivirus),
                "Registration alone does not confirm active protection. Open Windows Security to inspect each provider.", FindingSeverity.Info, "windowsSecurity");
        if (d.ProviderHealth == AntivirusProviderHealth.Good)
        {
            yield return new SecurityFinding(section, "Windows Security reports antivirus protection healthy",
                "The antivirus category reports good health. This does not certify every registered antivirus product.", FindingSeverity.Info);
            if (d.AntivirusEnabled != true || d.RealTimeProtection != true) yield break;
        }
        if (d.ProviderHealth is AntivirusProviderHealth.Poor or AntivirusProviderHealth.Snooze)
            yield return new SecurityFinding(section, "Windows Security reports antivirus protection needs attention",
                d.ProviderHealth == AntivirusProviderHealth.Snooze ? "Antivirus protection is snoozed." : "Antivirus protection reports poor health; protection or definitions may be unavailable.",
                FindingSeverity.High, "windowsSecurity");
        if (d.Error != null && d.AntivirusEnabled is null)
        {
            yield return new SecurityFinding(section, "Antivirus status unknown", d.Error, FindingSeverity.Medium, "windowsSecurity");
            yield break;
        }
        if (d.AntivirusEnabled == false || d.RealTimeProtection == false)
            yield return new SecurityFinding(section, "Real-time protection is off", "Microsoft Defender real-time protection is off. Check Windows Security to confirm another provider is actively protecting this PC.", FindingSeverity.High, "windowsSecurity");
        else if ((d.ProviderHealth is null or AntivirusProviderHealth.NotMonitored) && d.OtherAntivirus.Count > 0)
            yield return new SecurityFinding(section, "Other antivirus protection status unknown", "Windows Security could not confirm protection health for the registered antivirus products.", FindingSeverity.Medium, "windowsSecurity");
        if (d.SignatureAgeDays is > 7)
            yield return new SecurityFinding(section, $"Virus definitions are {d.SignatureAgeDays} days old", "Run Windows Update or \"Check for updates\" in Windows Security.", FindingSeverity.Medium, "windowsSecurity");
        if (d.QuickScanAgeDays is > 30)
            yield return new SecurityFinding(section, $"No quick scan for {d.QuickScanAgeDays} days", "Run a quick scan to check the places malware usually hides.", FindingSeverity.Info, "quickscan");
    }

    /// <summary>Why an extension deserves a look: installed outside the web store, or forced on by a policy.</summary>
    public static SecurityFinding? ExtensionFinding(BrowserExtensionInfo e)
    {
        const string section = "Browser extensions";
        if (e.IsComponent) return null;
        var where = $"{e.BrowserDisplayName} · {e.ProfileName}";
        if (e.InstalledByPolicy)
            return new SecurityFinding(section, $"{e.Name} is forced on by a policy", $"{where}. Malware often installs itself this way; ignore it if your employer manages this PC.", FindingSeverity.Medium, "extensions");
        if (!e.FromWebStore)
            return new SecurityFinding(section, $"{e.Name} was not installed from the web store", $"{where}. Side-loaded extensions skip the store's checks – remove it unless you added it yourself.", FindingSeverity.Medium, "extensions");
        return null;
    }

    /// <summary>Folders any program (or malware) can write to without administrator rights.</summary>
    public static bool IsUserWritableLocation(string? path, IEnumerable<string> userRoots) =>
        !string.IsNullOrWhiteSpace(path) && userRoots.Any(r => !string.IsNullOrEmpty(r) && PathUtil.IsUnder(path, r));
}

/// <summary>
/// "Malicious software &amp; extensions": Microsoft Defender's state and threats, extensions from outside the web
/// stores or forced by policy, and unsigned programs that start at sign-in from user-writable folders.
/// </summary>
public sealed class SecurityCheckService
{
    [DllImport("wscapi.dll", ExactSpelling = true)]
    private static extern int WscGetSecurityProviderHealth(uint providers, out AntivirusProviderHealth health);

    private static AntivirusProviderHealth? ReadAntivirusHealth()
    {
        try
        {
            // WSC_SECURITY_PROVIDER_ANTIVIRUS = 4. S_FALSE (service unavailable) is not healthy.
            return WscGetSecurityProviderHealth(4, out var health) == 0 && Enum.IsDefined(health) ? health : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    private const string DefenderScript = @"
$r=@{}
try { $s=Get-MpComputerStatus -ErrorAction Stop; $r.AntivirusEnabled=$s.AntivirusEnabled; $r.RealTime=$s.RealTimeProtectionEnabled; $r.SigAge=[int]$s.AntivirusSignatureAge; $r.QuickScanAge=[int]$s.QuickScanAge; $r.Mode=[string]$s.AMRunningMode } catch { $r.Error=$_.Exception.Message }
try { $r.Threats=@(Get-MpThreat -ErrorAction Stop | Where-Object { $_.IsActive } | ForEach-Object { [string]$_.ThreatName }) } catch { }
try { $r.OtherAv=@(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object { [string]$_.displayName }) } catch { }
$r | ConvertTo-Json -Compress";

    public async Task<List<SecurityFinding>> CheckAsync(BrowserExtensionService browser, StartupService startup, CancellationToken ct)
    {
        var findings = new List<SecurityFinding>();

        var defenderTask = Task.Run(async () =>
        {
            var res = await PowerShellRunner.RunScriptAsync(DefenderScript, ct, TimeSpan.FromMinutes(1)).ConfigureAwait(false);
            return SecurityRules.ParseDefender(res.StdOut.Trim().Split('\n').LastOrDefault(l => l.TrimStart().StartsWith('{')), ReadAntivirusHealth());
        }, ct);

        try
        {
            foreach (var e in await browser.GetExtensionsAsync(false, ct).ConfigureAwait(false))
                if (SecurityRules.ExtensionFinding(e) is { } f) findings.Add(f);
        }
        catch (Exception ex) { findings.Add(new SecurityFinding("Browser extensions", "Could not read extensions", ex.Message, FindingSeverity.Info)); }

        try { findings.AddRange(await Task.Run(() => UnsignedStartupFindings(startup), ct).ConfigureAwait(false)); }
        catch (Exception ex) { findings.Add(new SecurityFinding("Startup programs", "Could not read startup programs", ex.Message, FindingSeverity.Info)); }

        try { findings.InsertRange(0, SecurityRules.DefenderFindings(await defenderTask.ConfigureAwait(false))); }
        catch (Exception ex) { findings.Insert(0, new SecurityFinding("Antivirus", "Antivirus status unknown", ex.Message, FindingSeverity.Medium, "windowsSecurity")); }
        return findings;
    }

    private static IEnumerable<SecurityFinding> UnsignedStartupFindings(StartupService startup)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            Path.GetTempPath(), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            local, Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), SystemCleanupService.DownloadsFolder,
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "..", "Public")),
        };
        foreach (var item in startup.GetItems().Where(i => i.Enabled && i.TargetExists))
        {
            var exe = item.ExePath;
            if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe)) continue;
            if (!SecurityRules.IsUserWritableLocation(exe, roots)) continue;
            if (IsSigned(exe)) continue;
            yield return new SecurityFinding("Startup programs", $"{item.Name} starts an unsigned program from a user folder",
                $"{exe}. Legitimate software is usually signed and installed under Program Files; check it and switch it off in Startup Apps if you do not recognise it.",
                FindingSeverity.Medium, "startup");
        }
    }

    /// <summary>True when the file carries an Authenticode signature (validity is Windows SmartScreen's job).</summary>
    public static bool IsSigned(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the only API that reads an Authenticode signer without WinVerifyTrust
            using var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return cert != null;
        }
        catch (CryptographicException) { return false; }
        catch { return true; } // unreadable – do not accuse it
    }

    /// <summary>Runs a Microsoft Defender quick scan (a few minutes). Returns an error message or null.</summary>
    public async Task<string?> QuickScanAsync(CancellationToken ct)
    {
        var res = await PowerShellRunner.RunScriptAsync("Start-MpScan -ScanType QuickScan -ErrorAction Stop", ct, TimeSpan.FromMinutes(45)).ConfigureAwait(false);
        return res.Success ? null : (res.StdErr.Trim() is { Length: > 0 } e ? e : "The scan did not complete.");
    }
}
