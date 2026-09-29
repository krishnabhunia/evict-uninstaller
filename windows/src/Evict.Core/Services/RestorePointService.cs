using System.Management;
using Evict.Core.Models;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>Creates System Restore points through the WMI SystemRestore class (requires administrator rights).</summary>
public sealed class RestorePointService
{
    private const uint APPLICATION_UNINSTALL = 1;
    private const uint BEGIN_SYSTEM_CHANGE = 100;

    public static bool IsSystemRestoreEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            var v = key?.GetValue("RPSessionInterval");
            return v is int i ? i != 0 : true; // absent → assume enabled, WMI will tell us otherwise
        }
        catch { return true; }
    }

    /// <summary>
    /// Windows 8+ silently skips restore points created less than 24 h after the previous one unless
    /// SystemRestorePointCreationFrequency is 0. We report that so the UI can explain it.
    /// </summary>
    public static int CreationFrequencyMinutes()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
            return key?.GetValue("SystemRestorePointCreationFrequency") is int i ? i : 1440;
        }
        catch { return 1440; }
    }

    public Task<RestorePointResult> CreateAsync(string description, CancellationToken ct) =>
        Task.Run(() => Create(description), ct);

    public RestorePointResult Create(string description)
    {
        var result = new RestorePointResult { Attempted = true };
        if (!ElevationHelper.IsElevated)
        {
            result.Message = "Administrator rights are required to create a restore point.";
            return result;
        }
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            scope.Connect();
            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions());
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description.Length > 64 ? description[..64] : description;
            inParams["RestorePointType"] = APPLICATION_UNINSTALL;
            inParams["EventType"] = BEGIN_SYSTEM_CHANGE;
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            uint rv = Convert.ToUInt32(outParams["ReturnValue"]);
            result.ReturnCode = rv;
            result.Succeeded = rv == 0;
            result.Message = rv switch
            {
                0 => CreationFrequencyMinutes() > 0
                    ? "Restore point requested. Note: Windows skips a new point if one was created in the last 24 hours."
                    : "Restore point created.",
                1058 => "System Restore is disabled on this computer (service not running).",
                0x80070422 => "System Restore is disabled on this computer.",
                _ => $"System Restore returned code {rv}.",
            };
        }
        catch (ManagementException ex)
        {
            result.Message = "System Restore is unavailable: " + ex.Message;
            Log.Warn("Restore point failed: " + ex);
        }
        catch (Exception ex)
        {
            result.Message = "Could not create a restore point: " + ex.Message;
            Log.Warn("Restore point failed: " + ex);
        }
        return result;
    }
}
