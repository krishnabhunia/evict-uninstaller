using Evict.Core.Services;

namespace Evict.App.Services;

/// <summary>Resolves a .lnk shortcut to its target path using the Windows Script Host COM object (late-bound).</summary>
public static class ShortcutResolver
{
    public static string? ResolveTarget(string lnkPath)
    {
        if (string.IsNullOrWhiteSpace(lnkPath) || !lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return lnkPath;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve shortcut {lnkPath}: {ex.Message}");
            return null;
        }
    }
}
