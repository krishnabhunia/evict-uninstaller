using System.Text.Json.Serialization;

namespace Evict.Core.Models;

public enum SoftwareUpdateOutcome { InProgress, Updated, Failed, RestartRequired, Cancelled, Interrupted }

/// <summary>A single update attempt, independent of future package versions.</summary>
public sealed record SoftwareUpdateAttempt
{
    [JsonRequired] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonRequired] public string Name { get; init; } = "";
    [JsonRequired] public string PackageId { get; init; } = "";
    public string? Source { get; init; }
    public string? PreviousVersion { get; init; }
    public string? TargetVersion { get; init; }
    [JsonRequired] public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; init; }
    [JsonRequired] public SoftwareUpdateOutcome Outcome { get; init; } = SoftwareUpdateOutcome.InProgress;
    public int? ExitCode { get; init; }
    [JsonRequired] public string Message { get; init; } = "";
    [JsonRequired] public IReadOnlyList<string> Output { get; init; } = Array.Empty<string>();
}

public sealed record SoftwareUpgradeResult(SoftwareUpdateOutcome Outcome, string Message, int? ExitCode = null)
{
    public bool Ok => Outcome is SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.RestartRequired;
}
