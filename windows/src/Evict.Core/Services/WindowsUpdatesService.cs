using System.Globalization;
using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Lists installed Windows updates (Win32_QuickFixEngineering via Get-HotFix) and uninstalls them with wusa.exe.</summary>
public sealed class WindowsUpdatesService
{
    private readonly Func<bool> _isElevated;
    private readonly Func<string, string, CancellationToken, Task<int?>> _runUninstaller;
    private readonly Func<CancellationToken, Task<(List<WindowsUpdateInfo> Updates, string? Error)>> _readUpdates;

    public WindowsUpdatesService()
    {
        _isElevated = () => ElevationHelper.IsElevated;
        _runUninstaller = (exe, args, ct) => ProcessRunner.RunShellAndWaitAsync(exe, args, null, ct);
        _readUpdates = GetUpdatesAsync;
    }

    internal WindowsUpdatesService(Func<bool> isElevated,
        Func<string, string, CancellationToken, Task<int?>> runUninstaller,
        Func<CancellationToken, Task<(List<WindowsUpdateInfo> Updates, string? Error)>> readUpdates)
    {
        _isElevated = isElevated;
        _runUninstaller = runUninstaller;
        _readUpdates = readUpdates;
    }

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

    /// <summary>Supported interactive WUSA removal. The user sees Windows' confirmation and restart prompts.</summary>
    public async Task<(bool Ok, string Message, bool RebootRequired)> UninstallAsync(WindowsUpdateInfo update, CancellationToken ct)
    {
        if (!_isElevated()) return (false, "Administrator rights are required to uninstall Windows updates.", false);
        var kb = Regex.Match(update.HotFixId, @"^KB([1-9]\d*)$", RegexOptions.IgnoreCase).Groups[1].Value;
        if (kb.Length == 0) return (false, "Invalid KB number.", false);
        var wusa = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wusa.exe");
        int? exitCode;
        try { exitCode = await _runUninstaller(wusa, $"/uninstall /kb:{kb}", ct).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Windows update removal was cancelled.", false);
        }
        if (exitCode == 0)
        {
            var (remaining, error) = await _readUpdates(ct).ConfigureAwait(false);
            if (error != null) return (false, $"The command completed, but removal of KB{kb} could not be verified: {error}", false);
            if (remaining.Any(u => u.HotFixId.Equals(update.HotFixId, StringComparison.OrdinalIgnoreCase)))
                return (false, $"KB{kb} still appears installed. Removal was not verified; check Windows Settings and any restart prompt.", false);
            return (true, $"KB{kb} uninstalled and verified absent.", false);
        }
        return exitCode switch
        {
            3010 or 1641 => (true, $"Removal of KB{kb} requires a restart to finish; its final state must be checked afterwards.", true),
            1223 or unchecked((int)0x800704C7) or unchecked((int)0x8024000B) => (false, "Windows update removal was cancelled.", false),
            2359303 or unchecked((int)0x80240017) => (false, $"KB{kb} is not installed or cannot be uninstalled.", false),
            unchecked((int)0x800f0905) => (false, "This update is part of the servicing stack and cannot be removed.", false),
            unchecked((int)0x800f0825) => (false, "This update is permanent and cannot be uninstalled.", false),
            null => (false, "Windows did not return an update-removal process to monitor.", false),
            _ => (false, $"wusa exited with code {exitCode} (0x{exitCode:X8}).", false),
        };
    }
}
