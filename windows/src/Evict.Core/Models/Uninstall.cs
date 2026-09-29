namespace Evict.Core.Models;

public enum UninstallMethod
{
    Standard,
    Quiet,
    Force,
    InstallLog,
    RegistryEntryOnly,
}

/// <summary>Everything the leftover scanner needs, captured *before* the uninstaller deletes the registry entry.</summary>
public sealed class ProgramFingerprint
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? KeyName { get; init; }
    public string? MsiProductCode { get; init; }
    public string? UninstallExePath { get; init; }
    public string? PrimaryExecutable { get; init; }
    public string? IconPath { get; init; }
    public string? RegistryPath { get; init; }
    public RegistryScope? Scope { get; init; }

    /// <summary>Executable file names (without directory) known to belong to this program.</summary>
    public List<string> ExecutableNames { get; init; } = new();

    /// <summary>Normalized name keys used for folder / registry key matching, most specific first.</summary>
    public List<Util.CandidateKey> NameKeys { get; init; } = new();
    public string? PublisherToken { get; init; }
}

public sealed class UninstallRunResult
{
    public int? ExitCode { get; set; }
    public TimeSpan Duration { get; set; }
    public bool RegistryEntryRemoved { get; set; }
    public bool Launched { get; set; }
    public bool Cancelled { get; set; }
    public string? Command { get; set; }
    public string? Error { get; set; }
    public string? Note { get; set; }

    /// <summary>Best-effort success: the process ran and the entry disappeared, or exit code 0 / 3010 / 1641.</summary>
    public bool LikelySucceeded =>
        Launched && !Cancelled && (RegistryEntryRemoved || ExitCode is 0 or 3010 or 1641);
}

public sealed class UninstallHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public required string ProgramName { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public UninstallMethod Method { get; init; }
    public int? ExitCode { get; init; }
    public bool Succeeded { get; init; }
    public int LeftoversFound { get; init; }
    public int LeftoversRemoved { get; init; }
    public long BytesReclaimed { get; init; }
    public string? Notes { get; init; }
    public string? InstallLocation { get; init; }
}

public sealed class RestorePointResult
{
    public bool Attempted { get; set; }
    public bool Succeeded { get; set; }
    public uint ReturnCode { get; set; }
    public string Message { get; set; } = "";
}
