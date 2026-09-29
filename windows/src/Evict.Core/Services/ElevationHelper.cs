using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Evict.Core.Services;

public static class ElevationHelper
{
    private static bool? _isElevated;

    public static bool IsElevated
    {
        get
        {
            if (_isElevated is { } v) return v;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isElevated = false;
            }
            return _isElevated.Value;
        }
    }

    /// <summary>
    /// Re-launches the current executable with the UAC "runas" verb. Returns true when the new process
    /// started (caller should then shut down), false when the user cancelled the prompt.
    /// </summary>
    public static bool RestartElevated(string? extraArgs = null)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = extraArgs ?? "",
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false; // user declined UAC
        }
        catch
        {
            return false;
        }
    }
}
