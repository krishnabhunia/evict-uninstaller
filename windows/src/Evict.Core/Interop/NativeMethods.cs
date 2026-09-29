using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Evict.Core.Interop;

internal static class NativeMethods
{
    // ───────────────────────────── Registry ─────────────────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryInfoKeyW(
        SafeRegistryHandle hKey,
        IntPtr lpClass,
        IntPtr lpcchClass,
        IntPtr lpReserved,
        IntPtr lpcSubKeys,
        IntPtr lpcbMaxSubKeyLen,
        IntPtr lpcbMaxClassLen,
        IntPtr lpcValues,
        IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor,
        out System.Runtime.InteropServices.ComTypes.FILETIME lpftLastWriteTime);

    /// <summary>Last-write timestamp of a registry key (local time), or null on failure.</summary>
    public static DateTime? GetRegistryKeyLastWriteTime(RegistryKey key)
    {
        try
        {
            int rc = RegQueryInfoKeyW(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ft);
            if (rc != 0) return null;
            long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            if (ticks <= 0) return null;
            return DateTime.FromFileTimeUtc(ticks).ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle hKey, string lpValueName, IntPtr lpReserved, out uint lpType, byte[]? lpData, ref uint lpcbData);

    private const int ERROR_MORE_DATA = 234;

    /// <summary>A value's raw registry type and bytes (exactly as stored), or null when it does not exist.</summary>
    public static (uint Type, byte[] Data)? ReadRawRegistryValue(RegistryKey key, string name)
    {
        uint size = 0;
        int rc = RegQueryValueExW(key.Handle, name, IntPtr.Zero, out var type, null, ref size);
        if (rc != 0 && rc != ERROR_MORE_DATA) return null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var data = new byte[size];
            uint got = size;
            rc = RegQueryValueExW(key.Handle, name, IntPtr.Zero, out type, data, ref got);
            if (rc == 0) return (type, got == size ? data : data[..(int)got]);
            if (rc != ERROR_MORE_DATA) return null;
            size = got; // the value grew between the two calls
        }
        return null;
    }

    // ───────────────────────────── Shell file operations (Recycle Bin) ─────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_NOCONFIRMMKDIR = 0x0200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);

    /// <summary>Sends a file or folder to the Recycle Bin. Returns 0 on success, otherwise a shell error code.</summary>
    public static int SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",          // double-null terminated list
            pTo = null,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_NOCONFIRMMKDIR),
        };
        int rc = SHFileOperationW(ref op);
        if (rc == 0 && op.fAnyOperationsAborted) rc = 1223; // ERROR_CANCELLED
        return rc;
    }

    // ───────────────────────────── Windows ─────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WM_CLOSE = 0x0010;

    /// <summary>
    /// Posts WM_CLOSE to every top-level window of the process – visible or hidden (notification-area programs keep a
    /// hidden one) – which is what "taskkill" without /F does. Returns how many windows were asked.
    /// </summary>
    public static int PostCloseToProcessWindows(int pid)
    {
        int count = 0;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out var owner);
                if (owner == (uint)pid && PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) count++;
                return true;
            }, IntPtr.Zero);
        }
        catch { /* not on Windows / window gone */ }
        return count;
    }

    // ───────────────────────────── Token ─────────────────────────────

    private const int TokenElevationTypeClass = 18;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    /// <summary>TOKEN_ELEVATION_TYPE of this process: 1 = default (no split token), 2 = full (elevated), 3 = limited (UAC-filtered administrator); 0 on failure.</summary>
    public static int GetTokenElevationType()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return GetTokenInformation(identity.Token, TokenElevationTypeClass, out var type, sizeof(int), out _) ? type : 0;
        }
        catch
        {
            return 0;
        }
    }

    // ───────────────────────────── Processes ─────────────────────────────

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    /// <summary>Full image path of a process, using the limited-information access right (works across sessions for most processes).</summary>
    public static string? GetProcessImagePath(int pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (h != IntPtr.Zero) CloseHandle(h);
        }
    }

    // ───────────────────────────── Misc ─────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileExW(string lpExistingFileName, string? lpNewFileName, uint dwFlags);

    public const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x4;
}
