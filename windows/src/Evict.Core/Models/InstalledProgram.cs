using Microsoft.Win32;

namespace Evict.Core.Models;

/// <summary>Where the program's registry entry lives.</summary>
public enum RegistryScope
{
    /// <summary>HKLM, 64-bit view.</summary>
    Machine64,
    /// <summary>HKLM, 32-bit view (WOW6432Node).</summary>
    Machine32,
    /// <summary>HKCU (per-user install).</summary>
    User,
}

/// <summary>Installer technology detected for a program.</summary>
public enum InstallerKind
{
    Unknown,
    Msi,
    InnoSetup,
    Nsis,
    InstallShield,
    WiseInstaller,
    SquirrelOrElectron,
    Other,
}

/// <summary>
/// One row of the classic "Programs and Features" list, read from the Uninstall registry keys.
/// Plain mutable POCO: some fields (size, last-used) are filled in later by background workers.
/// </summary>
public sealed class InstalledProgram
{
    /// <summary>Stable identifier: scope + key name.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; set; }
    public string? DisplayVersion { get; set; }
    public string? Publisher { get; set; }
    public string? InstallLocation { get; set; }
    public string? InstallSource { get; set; }
    public string? UninstallString { get; set; }
    public string? QuietUninstallString { get; set; }
    public string? ModifyPath { get; set; }
    public string? DisplayIcon { get; set; }
    public string? UrlInfoAbout { get; set; }
    public string? HelpLink { get; set; }
    public string? Comments { get; set; }

    /// <summary>Registry key name under ...\Uninstall (for MSI this is the product code GUID).</summary>
    public required string KeyName { get; init; }
    public required RegistryScope Scope { get; init; }

    /// <summary>Human readable full registry path, e.g. HKLM\SOFTWARE\WOW6432Node\...\Uninstall\Foo.</summary>
    public required string RegistryPath { get; init; }

    public DateTime? InstallDate { get; set; }
    public DateTime? RegistryKeyLastWrite { get; set; }

    /// <summary>Size in bytes as reported by EstimatedSize (KB) or computed from the install folder.</summary>
    public long? SizeBytes { get; set; }
    /// <summary>True when <see cref="SizeBytes"/> was computed by walking the install folder.</summary>
    public bool SizeIsMeasured { get; set; }

    public bool IsMsi { get; set; }
    public string? MsiProductCode { get; set; }
    public bool IsSystemComponent { get; set; }
    public InstallerKind Installer { get; set; } = InstallerKind.Unknown;

    /// <summary>Best guess of the main executable (from DisplayIcon or the install folder).</summary>
    public string? PrimaryExecutable { get; set; }

    /// <summary>Last time an executable belonging to this program was launched (UserAssist heuristic).</summary>
    public DateTime? LastUsed { get; set; }
    public int RunCount { get; set; }

    /// <summary>Set by the bundleware heuristic when several unrelated programs were installed within minutes.</summary>
    public bool IsBundleSuspect { get; set; }
    public string? BundleGroupNote { get; set; }
    /// <summary>Name matches the known-bundleware list (see KnownBundleware).</summary>
    public bool IsKnownBundleware { get; set; }

    /// <summary>True when neither the install folder nor the uninstaller executable exist any more.</summary>
    public bool IsBrokenEntry { get; set; }

    public bool HasUninstaller => !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString) || IsMsi;

    public bool Is32Bit => Scope == RegistryScope.Machine32;
    public bool IsPerUser => Scope == RegistryScope.User;

    public DateTime? EffectiveInstallDate => InstallDate ?? RegistryKeyLastWrite;

    public RegistryHive Hive => Scope == RegistryScope.User ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
    public RegistryView View => Scope == RegistryScope.Machine32 ? RegistryView.Registry32 : RegistryView.Registry64;

    public override string ToString() => $"{DisplayName} {DisplayVersion} ({Publisher})";
}
