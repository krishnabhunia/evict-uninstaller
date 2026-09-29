using Microsoft.Win32;

namespace Evict.Core.Models;

public enum LeftoverKind
{
    Folder,
    File,
    Shortcut,
    RegistryKey,
    RegistryValue,
    StartupEntry,
    Service,
    ScheduledTask,
}

public enum LeftoverConfidence
{
    /// <summary>Directly tied to the program (install folder, its own uninstall key, shortcut pointing into it).</summary>
    High,
    /// <summary>Matched by program / publisher name — almost always correct, worth a glance.</summary>
    Medium,
    /// <summary>Fuzzy name match — review before deleting.</summary>
    Low,
}

/// <summary>A file, folder or registry remnant found after (or instead of) a normal uninstall.</summary>
public sealed class LeftoverItem
{
    public required LeftoverKind Kind { get; init; }

    /// <summary>Display path: filesystem path, or HKxx\...\Key[\\ValueName].</summary>
    public required string Path { get; init; }

    public long SizeBytes { get; set; }
    public string? Detail { get; init; }
    public LeftoverConfidence Confidence { get; init; } = LeftoverConfidence.Medium;

    /// <summary>Program display name this leftover belongs to (for grouping in batch mode).</summary>
    public string? ProgramName { get; init; }

    // Registry specifics
    public RegistryHive? Hive { get; init; }
    public RegistryView RegView { get; init; } = RegistryView.Default;
    public string? SubKey { get; init; }
    public string? ValueName { get; init; }

    // Service / task specifics
    public string? ServiceName { get; init; }
    public string? TaskName { get; init; }

    public bool IsFileSystem => Kind is LeftoverKind.Folder or LeftoverKind.File or LeftoverKind.Shortcut;
    public bool IsRegistry => Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.StartupEntry;

    public override string ToString() => $"[{Kind}] {Path}";
}

public sealed class LeftoverScanResult
{
    public List<LeftoverItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    public TimeSpan Elapsed { get; set; }
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
    public int FileSystemCount => Items.Count(i => i.IsFileSystem);
    public int RegistryCount => Items.Count(i => i.IsRegistry);
}

public sealed class LeftoverScanOptions
{
    /// <summary>Scan AppData of every user profile (requires administrator rights).</summary>
    public bool ScanAllUserProfiles { get; set; } = true;
    public bool ScanRegistry { get; set; } = true;
    public bool ScanServices { get; set; } = true;
    public bool ScanScheduledTasks { get; set; } = true;
    public bool ScanShortcuts { get; set; } = true;
    /// <summary>Include low-confidence fuzzy matches.</summary>
    public bool IncludeLowConfidence { get; set; } = true;
}

public sealed class CleanupResult
{
    public int Removed { get; set; }
    public int Failed { get; set; }
    public long BytesReclaimed { get; set; }
    /// <summary>Registry keys/values deleted and confirmed absent afterwards.</summary>
    public int RegistryVerified { get; set; }
    public List<(LeftoverItem Item, string Error)> Errors { get; } = new();
}
