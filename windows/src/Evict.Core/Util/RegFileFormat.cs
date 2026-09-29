using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Evict.Core.Util;

/// <summary>One registry value as stored: raw type number (REG_SZ = 1, REG_DWORD = 4 …) and raw bytes.</summary>
public sealed record RegRawValue(string Name, uint Type, byte[] Data);

/// <summary>A key block of a .reg file: "[HKEY_…\path]" followed by some or all of its values.</summary>
public sealed record RegKeyBlock(string FullPath, IReadOnlyList<RegRawValue> Values);

/// <summary>
/// Writes regedit's "Windows Registry Editor Version 5.00" format (what regedit exports and <c>reg import</c> reads).
/// Values are written from their raw type + bytes, so every type round-trips exactly. Pure logic – unit tested.
/// </summary>
public static class RegFileFormat
{
    public const string Header = "Windows Registry Editor Version 5.00";

    public const uint RegNone = 0, RegSz = 1, RegExpandSz = 2, RegBinary = 3, RegDword = 4, RegMultiSz = 7, RegQword = 11;

    public static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => throw new ArgumentOutOfRangeException(nameof(hive), hive, "Unsupported hive"),
    };

    /// <summary>Full key path as a 64-bit regedit sees it: 32-bit HKLM\SOFTWARE keys live under WOW6432Node.</summary>
    public static string KeyPath(RegistryHive hive, RegistryView view, string subKey)
    {
        var sk = subKey.Trim('\\');
        if (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine
            && sk.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
            && !sk.StartsWith(@"SOFTWARE\WOW6432Node", StringComparison.OrdinalIgnoreCase))
        {
            sk = @"SOFTWARE\WOW6432Node\" + sk[9..];
        }
        else if (view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine && sk.Equals("SOFTWARE", StringComparison.OrdinalIgnoreCase))
        {
            sk = @"SOFTWARE\WOW6432Node";
        }
        return sk.Length == 0 ? HiveName(hive) : $@"{HiveName(hive)}\{sk}";
    }

    public static string QuoteName(string name) => name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>"name"=data line for one value.</summary>
    public static string FormatValue(RegRawValue v)
    {
        var name = QuoteName(v.Name);
        switch (v.Type)
        {
            case RegSz:
            {
                var text = DecodeString(v.Data);
                // Strings regedit cannot write in quotes (embedded NULs or line breaks) are exported as hex(1).
                if (text != null && !text.Any(c => c < 0x20)) return $"{name}=\"{Escape(text)}\"";
                return $"{name}=hex(1):{Hex(v.Data)}";
            }
            case RegDword when v.Data.Length == 4:
                return $"{name}=dword:{BitConverter.ToUInt32(v.Data, 0).ToString("x8", CultureInfo.InvariantCulture)}";
            case RegBinary:
                return $"{name}=hex:{Hex(v.Data)}";
            default:
                return $"{name}=hex({v.Type.ToString("x", CultureInfo.InvariantCulture)}):{Hex(v.Data)}";
        }
    }

    /// <summary>UTF-16 string without its terminating NUL; null when the data is not valid UTF-16 text.</summary>
    private static string? DecodeString(byte[] data)
    {
        if (data.Length % 2 != 0) return null;
        var s = Encoding.Unicode.GetString(data);
        if (s.EndsWith('\0')) s = s[..^1];
        return s;
    }

    private static string Hex(byte[] data) => string.Join(",", data.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    public static string BuildDocument(IEnumerable<RegKeyBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n\r\n");
        foreach (var b in blocks)
        {
            sb.Append('[').Append(b.FullPath).Append("]\r\n");
            foreach (var v in b.Values) sb.Append(FormatValue(v)).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>regedit writes UTF-16 LE with a BOM; <c>reg import</c> expects the same.</summary>
    public static byte[] Encode(string document) => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(document)).ToArray();

    /// <summary>Helpers that build raw values the way Windows stores them (used by tests and callers).</summary>
    public static RegRawValue String(string name, string value) => new(name, RegSz, Encoding.Unicode.GetBytes(value + "\0"));
    public static RegRawValue ExpandString(string name, string value) => new(name, RegExpandSz, Encoding.Unicode.GetBytes(value + "\0"));
    public static RegRawValue MultiString(string name, IEnumerable<string> values) =>
        new(name, RegMultiSz, Encoding.Unicode.GetBytes(string.Concat(values.Select(v => v + "\0")) + "\0"));
    public static RegRawValue Dword(string name, uint value) => new(name, RegDword, BitConverter.GetBytes(value));
    public static RegRawValue Qword(string name, ulong value) => new(name, RegQword, BitConverter.GetBytes(value));

    /// <summary>File-name-safe label: "2026-09-29_101530 Uninstall Foo.reg".</summary>
    public static string BackupFileName(DateTime when, string label)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var clean = new string(label.Select(c => invalid.Contains(c) || c < 0x20 ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 60) clean = clean[..60].Trim();
        if (clean.Length == 0) clean = "backup";
        return $"{when:yyyy-MM-dd_HHmmss} {clean}.reg";
    }

    /// <summary>Inverse of <see cref="BackupFileName"/>: the label and time, or null for a foreign file name.</summary>
    public static (DateTime When, string Label)? ParseBackupFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length < 19 || name[17] != ' ') return null;
        if (!DateTime.TryParseExact(name[..17], "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) return null;
        return (when, name[18..]);
    }
}
