using Microsoft.Win32;

namespace Evict.Core.Util;

public static class RegistryPaths
{
    public static string HiveAbbreviation(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.ClassesRoot => "HKCR",
        RegistryHive.Users => "HKU",
        RegistryHive.CurrentConfig => "HKCC",
        _ => hive.ToString(),
    };

    /// <summary>Human readable path; 32-bit HKLM\SOFTWARE keys are shown under WOW6432Node.</summary>
    public static string Display(RegistryHive hive, RegistryView view, string subKey, string? valueName = null)
    {
        var sk = subKey.Trim('\\');
        if (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine
            && sk.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
            && !sk.StartsWith(@"SOFTWARE\WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            sk = @"SOFTWARE\WOW6432Node\" + sk[9..];
        }
        var s = $@"{HiveAbbreviation(hive)}\{sk}";
        if (valueName != null) s += $@" → ""{(valueName.Length == 0 ? "(Default)" : valueName)}""";
        return s;
    }

    public static string Join(string parent, string child) =>
        string.IsNullOrEmpty(parent) ? child : parent.TrimEnd('\\') + "\\" + child.TrimStart('\\');

    public static (string Parent, string Leaf) Split(string subKey)
    {
        var sk = subKey.Trim('\\');
        int i = sk.LastIndexOf('\\');
        return i < 0 ? ("", sk) : (sk[..i], sk[(i + 1)..]);
    }
}
