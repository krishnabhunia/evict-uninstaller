using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace Evict.Core.Util;

/// <summary>One registry value as stored: raw type number (REG_SZ = 1, REG_DWORD = 4 …) and raw bytes.</summary>
public sealed record RegRawValue(string Name, uint Type, byte[] Data);

/// <summary>A key block of a .reg file: "[HKEY_…\path]" followed by some or all of its values.</summary>
public sealed record RegKeyBlock(string FullPath, IReadOnlyList<RegRawValue> Values)
{
    public RegistryView View { get; init; } = RegistryView.Default;
}

public sealed record RegistryRestoreDocument(RegistryView View, string Document);

/// <summary>
/// Writes regedit's "Windows Registry Editor Version 5.00" format (what regedit exports and <c>reg import</c> reads).
/// Values are written from their raw type + bytes, so every type round-trips exactly. Pure logic – unit tested.
/// </summary>
public static class RegFileFormat
{
    public const string Header = "Windows Registry Editor Version 5.00";
    public const string ViewMarker = "; Evict registry view: ";
    public const string RestoreHint = "; Restore with Evict to preserve registry views. Manual import uses the importer's default view.";

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

    /// <summary>Logical registry path. The originating view is stored separately, including for shared keys.</summary>
    public static string KeyPath(RegistryHive hive, RegistryView view, string subKey)
    {
        var sk = subKey.Trim('\\');
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
        var list = blocks.ToList();
        bool preserveViews = list.Any(b => b.View != RegistryView.Default);
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n\r\n");
        if (preserveViews) sb.Append(RestoreHint).Append("\r\n\r\n");
        foreach (var b in list)
        {
            if (preserveViews)
                sb.Append(ViewMarker).Append(b.View switch { RegistryView.Registry32 => "32", RegistryView.Registry64 => "64", _ => "default" }).Append("\r\n");
            sb.Append('[').Append(b.FullPath).Append("]\r\n");
            foreach (var v in b.Values) sb.Append(FormatValue(v)).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Builds one import document per recorded view. Legacy unmarked .reg files retain their default-view behavior.</summary>
    public static IReadOnlyList<RegistryRestoreDocument> RestoreDocuments(string document)
    {
        if (!document.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith(Header, StringComparison.Ordinal))
            throw new FormatException("This is not a Windows Registry Editor backup.");
        if (!document.Contains(ViewMarker, StringComparison.Ordinal))
            return new[] { new RegistryRestoreDocument(RegistryView.Default, document) };

        var groups = new Dictionary<RegistryView, StringBuilder>();
        var hasKeys = new HashSet<RegistryView>();
        RegistryView view = RegistryView.Default;
        bool viewSpecified = false;
        foreach (var line in document.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith(ViewMarker, StringComparison.Ordinal))
            {
                view = line[ViewMarker.Length..].Trim() switch
                {
                    "32" => RegistryView.Registry32,
                    "64" => RegistryView.Registry64,
                    "default" => RegistryView.Default,
                    _ => throw new FormatException("The backup contains an invalid registry-view marker."),
                };
                viewSpecified = true;
                continue;
            }
            if (line.StartsWith('['))
            {
                if (!viewSpecified) throw new FormatException("A registry key in the backup has no recorded view.");
                hasKeys.Add(view);
            }
            if (!viewSpecified || line.TrimStart('\uFEFF').Equals(Header, StringComparison.Ordinal)) continue;
            if (!groups.TryGetValue(view, out var body)) groups[view] = body = new StringBuilder();
            body.Append(line).Append("\r\n");
        }
        if (hasKeys.Count == 0) throw new FormatException("The backup contains no registry keys.");
        return groups.Where(g => hasKeys.Contains(g.Key))
            .Select(g => new RegistryRestoreDocument(g.Key, Header + "\r\n\r\n" + g.Value)).ToList();
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
