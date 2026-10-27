namespace Evict.Core.Services;

/// <summary>An immutable view of the preferences used to authorize a background scan.</summary>
public sealed record ScheduledScanSnapshot(string Mode, int Hour, int Weekday, DateTime? AppliedUtc, DateTime? LastRunUtc)
{
    public bool IsEnabled => ScheduledScanTask.IsValidMode(Mode) && !Mode.Equals("Off", StringComparison.OrdinalIgnoreCase);

    public bool IsCatchUpDue(DateTime nowUtc)
    {
        if (!IsEnabled) return false;
        var baseline = LastRunUtc;
        if (AppliedUtc is { } applied && (baseline is null || applied > baseline.Value)) baseline = applied;
        if (baseline is null) return false;
        var period = Mode.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        return nowUtc - baseline.Value >= period + TimeSpan.FromHours(2);
    }
}

/// <summary>Authorizes each automatic scan and coalesces simultaneous scheduled/catch-up requests.</summary>
public sealed class ScheduledScanExecutionGuard
{
    private readonly Func<ScheduledScanSnapshot> _read;
    private readonly Func<ScheduledScanSnapshot, CancellationToken, Task<bool>> _matches;
    private readonly Func<CancellationToken, Task> _scan;
    private readonly Func<CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _running = new(1, 1);

    public ScheduledScanExecutionGuard(Func<ScheduledScanSnapshot> read,
        Func<ScheduledScanSnapshot, CancellationToken, Task<bool>> matches,
        Func<CancellationToken, Task> scan,
        Func<CancellationToken, Task>? delay = null, Func<DateTime>? utcNow = null)
    {
        _read = read;
        _matches = matches;
        _scan = scan;
        _delay = delay ?? (ct => Task.Delay(TimeSpan.FromSeconds(30), ct));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool IsRunning => _running.CurrentCount == 0;

    public async Task<bool> TryRunAsync(bool catchUp, CancellationToken ct = default)
    {
        if (!await _running.WaitAsync(0, ct)) return false;
        try
        {
            var initial = _read();
            if (!initial.IsEnabled || (catchUp && !initial.IsCatchUpDue(_utcNow()))) return false;
            if (!await _matches(initial, ct) || _read() != initial) return false;
            if (catchUp)
            {
                await _delay(ct);
                if (_read() != initial || !initial.IsCatchUpDue(_utcNow())) return false;
                if (!await _matches(initial, ct) || _read() != initial) return false;
            }
            ct.ThrowIfCancellationRequested();
            await _scan(ct);
            return true;
        }
        finally { _running.Release(); }
    }
}
