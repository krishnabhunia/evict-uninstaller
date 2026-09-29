namespace Evict.Core.Models;

// ───────────────────────────── Windows (Store / UWP) apps ─────────────────────────────

public sealed class AppxPackageInfo
{
    public required string Name { get; init; }
    public required string PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public string? DisplayName { get; set; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public string? PublisherDisplayName { get; set; }
    public string? InstallLocation { get; init; }
    public string? Architecture { get; init; }
    public bool IsFramework { get; init; }
    public bool IsBundle { get; init; }
    public bool NonRemovable { get; init; }
    /// <summary>Store, System, Developer, Enterprise, None.</summary>
    public string? SignatureKind { get; init; }
    public long? SizeBytes { get; set; }
    public DateTime? InstallDate { get; set; }
    public bool IsKnownBloatware { get; set; }
    public bool IsSystem => string.Equals(SignatureKind, "System", StringComparison.OrdinalIgnoreCase);
}

// ───────────────────────────── Browser extensions ─────────────────────────────

public enum BrowserKind { Chrome, Edge, Brave, Vivaldi, Opera, Chromium, Firefox }

public sealed class BrowserExtensionInfo
{
    public required BrowserKind Browser { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfilePath { get; init; }
    public required string ExtensionId { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; }
    public bool FromWebStore { get; init; }
    public bool InstalledByPolicy { get; init; }
    public bool IsComponent { get; init; }
    public DateTime? InstallTime { get; init; }
    public string? ExtensionPath { get; init; }
    public string? HomepageUrl { get; init; }
    public List<string> Permissions { get; init; } = new();
    public long SizeBytes { get; set; }

    public string BrowserDisplayName => Browser switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Brave => "Brave",
        BrowserKind.Vivaldi => "Vivaldi",
        BrowserKind.Opera => "Opera",
        BrowserKind.Chromium => "Chromium",
        BrowserKind.Firefox => "Mozilla Firefox",
        _ => Browser.ToString(),
    };

    public string Source => InstalledByPolicy ? "Policy" : FromWebStore ? "Web Store" : IsComponent ? "Built-in" : "Side-loaded";
}

// ───────────────────────────── Windows updates ─────────────────────────────

public sealed class WindowsUpdateInfo
{
    public required string HotFixId { get; init; }
    public string? Description { get; init; }
    public DateTime? InstalledOn { get; init; }
    public string? InstalledBy { get; init; }
    public string? Caption { get; init; }
    public string KbNumber => HotFixId.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? HotFixId[2..] : HotFixId;
}

// ───────────────────────────── winget ─────────────────────────────

public sealed class UpgradablePackage
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string? InstalledVersion { get; init; }
    public string? AvailableVersion { get; init; }
    public string? Source { get; init; }
}

// ───────────────────────────── Install monitor ─────────────────────────────

public sealed class InstallLog
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Title { get; set; }
    public required string InstallerPath { get; init; }
    public DateTime Started { get; init; }
    public DateTime? Finished { get; set; }
    public int? InstallerExitCode { get; set; }
    public List<string> CreatedFiles { get; set; } = new();
    public List<string> CreatedDirectories { get; set; } = new();
    public List<string> ModifiedFiles { get; set; } = new();
    public List<string> CreatedRegistryKeys { get; set; } = new();
    public List<string> NewUninstallEntries { get; set; } = new();
    public long TotalBytes { get; set; }
    public bool Uninstalled { get; set; }
}

// ───────────────────────────── Shredder ─────────────────────────────

public enum ShredMethod
{
    /// <summary>1 pass, zeros.</summary>
    Quick = 1,
    /// <summary>3 passes: zeros, ones, random (DoD 5220.22-M style).</summary>
    Dod3Pass = 3,
    /// <summary>7 passes alternating patterns and random.</summary>
    Dod7Pass = 7,
}

public sealed class ShredResult
{
    public int FilesShredded { get; set; }
    public int FoldersRemoved { get; set; }
    public long BytesOverwritten { get; set; }
    public List<(string Path, string Error)> Errors { get; } = new();
}

// ───────────────────────────── Progress ─────────────────────────────

public readonly record struct ProgressReport(string Message, double? Percent = null);
