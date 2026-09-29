using System.Text;
using Microsoft.Win32;

namespace Evict.Core.Services;

public sealed record UserAssistEntry(string Path, int RunCount, DateTime? LastRun);

/// <summary>
/// Reads Explorer's UserAssist counters – the same data the Start menu uses to sort "most used" –
/// to estimate when a program was last launched. Values are ROT13-encoded paths with known-folder GUIDs.
/// </summary>
public static class UserAssistReader
{
    private const string UserAssistRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    // Known-folder GUIDs that appear in UserAssist paths → environment folders.
    private static readonly Dictionary<string, Func<string>> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.System),
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["{A520A1A4-1780-4FF6-BD18-167343C5AF16}"] = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ["{5E6C858F-0E22-4760-9AFE-EA3317B67173}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ["{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ["{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        ["{625B53C3-AB48-4EC1-BA1F-A1EF4146FC19}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        ["{A4115719-D62E-491D-AA7C-E74B8BE3B067}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        ["{FDD39AD0-238F-46AF-ADB4-6C85480369C7}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        ["{374DE290-123F-4565-9164-39C4925E467B}"] = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ["{C4AA340D-F20F-4863-AFEF-F87EF2E6BA25}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    };

    public static string Rot13(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (c is >= 'a' and <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c is >= 'A' and <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Replaces a leading {KNOWNFOLDER-GUID} with the real folder path.</summary>
    public static string ExpandKnownFolder(string path)
    {
        if (path.Length > 38 && path[0] == '{')
        {
            int close = path.IndexOf('}');
            if (close == 37)
            {
                var guid = path[..38];
                if (KnownFolders.TryGetValue(guid, out var resolver))
                {
                    try
                    {
                        var root = resolver();
                        if (!string.IsNullOrEmpty(root)) return root + path[38..];
                    }
                    catch { /* fall through */ }
                }
            }
        }
        return path;
    }

    /// <summary>
    /// Parses the 72-byte Windows 7+ UserAssist value: run count at offset 4, last-run FILETIME at offset 60.
    /// </summary>
    public static (int RunCount, DateTime? LastRun)? ParseValue(byte[]? data)
    {
        if (data is null || data.Length < 68) return null;
        int count = BitConverter.ToInt32(data, 4);
        long ft = BitConverter.ToInt64(data, 60);
        DateTime? last = null;
        if (ft > 0)
        {
            try { last = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { last = null; }
            if (last is { } d && (d.Year < 1990 || d > DateTime.Now.AddDays(1))) last = null;
        }
        return (Math.Max(0, count), last);
    }

    /// <summary>Reads all executable entries for the current user. Never throws.</summary>
    public static List<UserAssistEntry> Read()
    {
        var list = new List<UserAssistEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(UserAssistRoot);
            if (root is null) return list;
            foreach (var guidName in root.GetSubKeyNames())
            {
                using var countKey = root.OpenSubKey(guidName + "\\Count");
                if (countKey is null) continue;
                foreach (var valueName in countKey.GetValueNames())
                {
                    if (valueName.Length == 0) continue;
                    var decoded = Rot13(valueName);
                    if (decoded.StartsWith("UEME_", StringComparison.OrdinalIgnoreCase)) continue; // XP-era counters
                    if (!decoded.Contains('\\') && !decoded.Contains(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = ExpandKnownFolder(decoded);
                    var parsed = ParseValue(countKey.GetValue(valueName) as byte[]);
                    if (parsed is null) continue;
                    list.Add(new UserAssistEntry(path, parsed.Value.RunCount, parsed.Value.LastRun));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("UserAssist read failed: " + ex.Message);
        }
        return list;
    }
}
