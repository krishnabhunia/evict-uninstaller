namespace Evict.Core.Models;

public enum SoftwareUpdateOutcome { InProgress, Updated, Failed, RestartRequired, Cancelled, Interrupted }

/// <summary>A single update attempt, independent of future package versions.</summary>
public sealed record SoftwareUpdateAttempt
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string PackageId { get; init; } = "";
    public string? Source { get; init; }
    public string? PreviousVersion { get; init; }
    public string? TargetVersion { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    public SoftwareUpdateOutcome Outcome { get; init; } = SoftwareUpdateOutcome.InProgress;
    public int? ExitCode { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Output { get; init; } = Array.Empty<string>();
}

public sealed record SoftwareUpgradeResult(SoftwareUpdateOutcome Outcome, string Message, int? ExitCode = null)
{
    public bool Ok => Outcome is SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.RestartRequired;
}
