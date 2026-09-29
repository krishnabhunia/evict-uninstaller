using Evict.Core.Interop;

namespace Evict.Core.Services;

/// <summary>Public helpers around process information (wraps the internal P/Invoke layer).</summary>
public static class ProcessUtil
{
    /// <summary>Full image path of a process or null (access denied / exited).</summary>
    public static string? GetImagePath(int pid) => NativeMethods.GetProcessImagePath(pid);
}
