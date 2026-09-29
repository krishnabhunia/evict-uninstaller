using System.Text.RegularExpressions;
using Evict.Core.Models;

namespace Evict.Core.Util;

public sealed record UninstallCommand(string FileName, string Arguments)
{
    public string Display => Arguments.Length == 0 ? Quote(FileName) : $"{Quote(FileName)} {Arguments}";
    private static string Quote(string s) => s.Contains(' ') && !s.StartsWith('"') ? $"\"{s}\"" : s;
}

/// <summary>
/// Splits the raw registry <c>UninstallString</c> into an executable and its arguments, and knows how to
/// turn common installer technologies into silent commands. Pure logic – unit tested.
/// </summary>
public static partial class UninstallCommandParser
{
    [GeneratedRegex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}")]
    public static partial Regex GuidRegex();

    [GeneratedRegex(@"\.(exe|com|bat|cmd|msi)(?=\s|$|"")", RegexOptions.IgnoreCase)]
    private static partial Regex ExeBoundaryRegex();

    public static bool IsGuid(string? s) => s != null && GuidRegex().IsMatch(s) && s.Trim().Length == 38;

    /// <summary>
    /// Parses a raw command line. Handles quoted paths, unquoted paths with spaces
    /// ("C:\Program Files\Foo\unins000.exe /SILENT"), and bare "MsiExec.exe /I{GUID}".
    /// </summary>
    public static UninstallCommand? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        try { s = Environment.ExpandEnvironmentVariables(s); } catch { /* ignore */ }

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 1)
            {
                var file = s[1..end];
                var args = s[(end + 1)..].Trim();
                return new UninstallCommand(file, args);
            }
            return new UninstallCommand(s.Trim('"'), "");
        }

        // Unquoted: split right after the first ".exe"/".msi" that is followed by whitespace or end.
        var m = ExeBoundaryRegex().Match(s);
        if (m.Success)
        {
            int cut = m.Index + m.Length;
            var file = s[..cut].Trim();
            var args = s[cut..].Trim();
            return new UninstallCommand(file, args);
        }

        // Fallback: first space.
        int sp = s.IndexOf(' ');
        return sp < 0 ? new UninstallCommand(s, "") : new UninstallCommand(s[..sp], s[(sp + 1)..].Trim());
    }

    public static bool IsMsiExec(UninstallCommand cmd) =>
        PathUtil.LeafName(cmd.FileName).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ||
        cmd.FileName.Equals("msiexec", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds the msiexec command for a product code. Quiet adds /qn /norestart.</summary>
    public static UninstallCommand BuildMsiUninstall(string productCode, bool quiet)
    {
        var msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
        if (!File.Exists(msiexec)) msiexec = "msiexec.exe";
        var args = $"/X{productCode}";
        if (quiet) args += " /qn /norestart";
        return new UninstallCommand(msiexec, args);
    }

    /// <summary>
    /// Normalises an msiexec command: /I → /X (install → uninstall), adds quiet switches when requested.
    /// </summary>
    public static UninstallCommand NormalizeMsiExec(UninstallCommand cmd, bool quiet)
    {
        var args = cmd.Arguments;
        // "/I{GUID}" or "/i {GUID}" or "/package {GUID}" → "/X{GUID}"
        args = Regex.Replace(args, @"(?i)(^|\s)/(?:i|package)\s*(\{)", "$1/X$2");
        if (quiet)
        {
            if (!Regex.IsMatch(args, @"(?i)(^|\s)/q")) args += " /qn";
            if (!Regex.IsMatch(args, @"(?i)(^|\s)/norestart")) args += " /norestart";
        }
        return cmd with { Arguments = args.Trim() };
    }

    /// <summary>Guesses the installer technology from the uninstaller file name and (optionally) its bytes.</summary>
    public static InstallerKind DetectInstaller(string? uninstallExe, Func<string, byte[]?>? readHead = null)
    {
        if (string.IsNullOrWhiteSpace(uninstallExe)) return InstallerKind.Unknown;
        var name = PathUtil.LeafName(uninstallExe.Trim('"'));

        if (name.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase)) return InstallerKind.Msi;
        if (Regex.IsMatch(name, @"(?i)^unins\d{3}\.exe$")) return InstallerKind.InnoSetup;
        if (name.Equals("Update.exe", StringComparison.OrdinalIgnoreCase)) return InstallerKind.SquirrelOrElectron;
        if (Regex.IsMatch(name, @"(?i)^(uninst|uninstall|uninstaller|un-install)\.exe$") && readHead == null) return InstallerKind.Nsis;

        if (readHead != null)
        {
            try
            {
                var head = readHead(uninstallExe);
                if (head != null)
                {
                    var text = System.Text.Encoding.ASCII.GetString(head);
                    if (text.Contains("Nullsoft", StringComparison.OrdinalIgnoreCase) || text.Contains("NSIS", StringComparison.Ordinal))
                        return InstallerKind.Nsis;
                    if (text.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase))
                        return InstallerKind.InnoSetup;
                    if (text.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
                        return InstallerKind.InstallShield;
                    if (text.Contains("Wise Installation", StringComparison.OrdinalIgnoreCase))
                        return InstallerKind.WiseInstaller;
                }
            }
            catch { /* unreadable */ }
            if (Regex.IsMatch(name, @"(?i)^(uninst|uninstall|uninstaller)\.exe$")) return InstallerKind.Nsis;
        }
        return InstallerKind.Other;
    }

    /// <summary>Silent switches for known installer technologies. Returns null if unknown (fall back to interactive).</summary>
    public static string? SilentSwitches(InstallerKind kind) => kind switch
    {
        InstallerKind.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        InstallerKind.Nsis => "/S",
        InstallerKind.InstallShield => "/s /v\"/qn\"",
        InstallerKind.WiseInstaller => "/s",
        InstallerKind.SquirrelOrElectron => "--uninstall -s",
        _ => null,
    };

    /// <summary>
    /// Chooses the best command for a program. Order: QuietUninstallString (when quiet) → MSI → UninstallString
    /// (+ silent switches for detected installers when quiet).
    /// </summary>
    public static UninstallCommand? Resolve(InstalledProgram p, bool quiet, Func<string, byte[]?>? readHead = null)
    {
        if (quiet && !string.IsNullOrWhiteSpace(p.QuietUninstallString))
        {
            var q = Parse(p.QuietUninstallString);
            if (q != null) return IsMsiExec(q) ? NormalizeMsiExec(q, true) : q;
        }

        if (p.IsMsi && !string.IsNullOrWhiteSpace(p.MsiProductCode))
            return BuildMsiUninstall(p.MsiProductCode, quiet);

        var cmd = Parse(p.UninstallString);
        if (cmd == null) return null;

        if (IsMsiExec(cmd)) return NormalizeMsiExec(cmd, quiet);

        if (quiet)
        {
            var kind = p.Installer != InstallerKind.Unknown ? p.Installer : DetectInstaller(cmd.FileName, readHead);
            var sw = SilentSwitches(kind);
            if (sw != null && !cmd.Arguments.Contains(sw.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
                cmd = cmd with { Arguments = (cmd.Arguments + " " + sw).Trim() };
        }
        return cmd;
    }
}
