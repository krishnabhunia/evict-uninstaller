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
    /// True for an administrator account running with UAC's limited token: elevating keeps the same account, profile and
    /// settings. False for a standard account – there UAC would run Evict under a different (administrator) account.
    /// </summary>
    public static bool CanElevateSameUser => Interop.NativeMethods.GetTokenElevationType() == 3;

    /// <summary>Re-launches elevated with these arguments (quoted for the Windows command line).</summary>
    public static bool RestartElevated(IReadOnlyList<string> args) => RestartElevated(Util.CommandLineOptions.JoinArguments(args));

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
