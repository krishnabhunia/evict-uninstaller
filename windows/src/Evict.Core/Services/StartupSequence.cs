namespace Evict.Core.Services;

/// <summary>A required startup operation. UI operations retain the caller's synchronization context.</summary>
public sealed record StartupStep(string Name, Func<CancellationToken, Task> Run);

/// <summary>Counts completed operations, not estimated elapsed-time percentages.</summary>
public sealed record StartupProgress(string Stage, int Completed, int Total)
{
    public bool IsComplete => Total > 0 && Completed == Total;
    public string Summary => $"{Completed} of {Total} stages complete";
}

public sealed class StartupStageException : Exception
{
    public string Stage { get; }
    public StartupStageException(string stage, Exception inner)
        : base($"Could not finish: {stage}.", inner) => Stage = stage;
}

/// <summary>Runs required steps in order. Failure/cancellation never advances the readiness gate.</summary>
public static class StartupSequence
{
    public static bool ShouldShowSplash(bool headless, bool selfCleanup, bool exit) => !headless && !selfCleanup && !exit;

    public static async Task RunAsync(IReadOnlyList<StartupStep> steps,
        Action<StartupProgress> report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(report);
        if (steps.Count == 0) throw new ArgumentException("At least one startup step is required.", nameof(steps));
        if (steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Run is null))
            throw new ArgumentException("Each startup step needs a name and operation.", nameof(steps));
        for (var i = 0; i < steps.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var step = steps[i];
            report(new(step.Name, i, steps.Count));
            try
            {
                await step.Run(ct);
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { throw new StartupStageException(step.Name, ex); }
            report(new(step.Name, i + 1, steps.Count));
        }
    }
}
