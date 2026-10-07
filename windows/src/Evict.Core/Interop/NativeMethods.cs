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

    // FOF_ALLOWUNDO alone permits permanent deletion when recycling is unavailable.
    // Windows 8+'s recycle-only flag explicitly requests recycling instead.
    internal const uint RecycleOnlyFlags = 0x00080000 /* FOFX_RECYCLEONDELETE */
        | 0x20000000 /* FOFX_ADDUNDORECORD */ | 0x00100000 /* FOFX_EARLYFAILURE */
        | 0x0400 /* FOF_NOERRORUI */ | 0x0004 /* FOF_SILENT */ | 0x0010 /* FOF_NOCONFIRMATION */;

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IntPtr sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId, out IShellItem item);

    /// <summary>Recycle-only operation. Non-recyclable volumes, failures and cancellation never trigger a hard delete.</summary>
    public static int SendToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return unchecked((int)0x80004001); // E_NOTIMPL
        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
                return unchecked((int)0x80070032); // ERROR_NOT_SUPPORTED: network/removable recycling is not supported
            if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return RecycleOnSta(path);
            int result = unchecked((int)0x80004005);
            var thread = new Thread(() => result = RecycleOnSta(path)) { IsBackground = true, Name = "Evict Recycle Bin" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return result;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private static int RecycleOnSta(string path)
    {
        object? operation = null;
        IShellItem? item = null;
        try
        {
            operation = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!);
            var op = (IFileOperation)operation!;
            int hr = op.SetOperationFlags(RecycleOnlyFlags);
            if (hr < 0) return hr;
            var iid = typeof(IShellItem).GUID;
            hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (hr < 0) return hr;
            hr = op.DeleteItem(item, IntPtr.Zero);
            if (hr < 0) return hr;
            hr = op.PerformOperations();
            if (hr < 0) return hr;
            hr = op.GetAnyOperationsAborted(out bool aborted);
            return hr < 0 ? hr : aborted ? unchecked((int)0x800704C7) : 0;
        }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
        finally
        {
            try { if (item != null) Marshal.FinalReleaseComObject(item); } catch { /* release only */ }
            try { if (operation != null) Marshal.FinalReleaseComObject(operation); } catch { /* release only */ }
        }
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
