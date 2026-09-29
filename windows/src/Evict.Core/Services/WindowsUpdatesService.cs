using System.Globalization;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Lists installed Windows updates (Win32_QuickFixEngineering via Get-HotFix) and uninstalls them with wusa.exe.</summary>
public sealed class WindowsUpdatesService
{
    private sealed class Row
    {
        public string? HotFixID { get; set; }
        public string? Description { get; set; }
        public string? InstalledOn { get; set; }
        public string? InstalledBy { get; set; }
        public string? Caption { get; set; }
    }

    public async Task<(List<WindowsUpdateInfo> Updates, string? Error)> GetUpdatesAsync(CancellationToken ct)
    {
        const string script = """
            $u = Get-HotFix | ForEach-Object {
              $d = $null; if ($_.InstalledOn) { $d = ([datetime]$_.InstalledOn).ToString('yyyy-MM-dd') }
              [pscustomobject]@{ HotFixID = $_.HotFixID; Description = $_.Description; InstalledOn = $d; InstalledBy = $_.InstalledBy; Caption = $_.Caption }
            }
            ConvertTo-Json -InputObject @($u) -Depth 2 -Compress
            """;
        var (rows, error) = await PowerShellRunner.RunJsonAsync<List<Row>>(script, ct, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        if (rows is null) return (new(), error);
        var list = rows.Where(r => !string.IsNullOrEmpty(r.HotFixID)).Select(r => new WindowsUpdateInfo
        {
            HotFixId = r.HotFixID!,
            Description = r.Description,
            InstalledBy = r.InstalledBy,
            Caption = r.Caption,
            InstalledOn = DateTime.TryParseExact(r.InstalledOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null,
        })
        .OrderByDescending(u => u.InstalledOn ?? DateTime.MinValue).ThenBy(u => u.HotFixId).ToList();
        return (list, error);
    }

    /// <summary>Runs "wusa /uninstall /kb:NNN /quiet /norestart". Exit 3010 = reboot required, 2359303 = not found.</summary>
    public async Task<(bool Ok, string Message, bool RebootRequired)> UninstallAsync(WindowsUpdateInfo update, CancellationToken ct)
    {
        if (!ElevationHelper.IsElevated) return (false, "Administrator rights are required to uninstall Windows updates.", false);
        var kb = Regex.Match(update.HotFixId, @"\d+").Value;
        if (kb.Length == 0) return (false, "Invalid KB number.", false);
        var wusa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wusa.exe");
        var res = await ProcessRunner.RunCapturedAsync(wusa, $"/uninstall /kb:{kb} /quiet /norestart", ct, TimeSpan.FromMinutes(20)).ConfigureAwait(false);
        return res.ExitCode switch
        {
            0 => (true, $"KB{kb} uninstalled.", false),
            3010 or 1641 => (true, $"KB{kb} uninstalled – a restart is required to finish.", true),
            2359303 or unchecked((int)0x80240017) => (false, $"KB{kb} is not installed or cannot be uninstalled.", false),
            unchecked((int)0x800f0905) => (false, "This update is part of the servicing stack and cannot be removed.", false),
            unchecked((int)0x800f0825) => (false, "This update is permanent and cannot be uninstalled.", false),
            _ => (false, $"wusa exited with code {res.ExitCode} (0x{res.ExitCode:X8}).", false),
        };
    }
}
