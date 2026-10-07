namespace Evict.Core.Services;

/// <summary>
/// Performs one delayed automatic check per application lifetime. Manual and startup checks share a
/// single in-flight request, and a manual check during startup replaces the pending automatic check.
/// The callers own cancellation and presentation; no remembered timestamp prevents the next restart.
/// </summary>
public sealed class UpdateCheckCoordinator
{
    private readonly Func<CancellationToken, bool, Task<UpdateCheckResult>> _check;
    private readonly Func<bool> _automaticChecksEnabled;
    private readonly Func<bool> _includePrereleases;
    private readonly Func<CancellationToken, Task> _startupDelay;
    private readonly object _gate = new();
    private int _startupRequested;
    private int _attempts;
    private int _checking;

    public UpdateCheckCoordinator(
        Func<CancellationToken, bool, Task<UpdateCheckResult>> check,
        Func<bool> automaticChecksEnabled,
        Func<bool> includePrereleases,
        Func<CancellationToken, Task>? startupDelay = null)
    {
        _check = check;
        _automaticChecksEnabled = automaticChecksEnabled;
        _includePrereleases = includePrereleases;
        _startupDelay = startupDelay ?? (ct => Task.Delay(TimeSpan.FromSeconds(4), ct));
    }

    public event Action<bool>? CheckingChanged;
    public bool IsChecking => Volatile.Read(ref _checking) != 0;

    public async Task<UpdateCheckResult?> CheckOnStartupAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _startupRequested, 1) != 0) return null;
        ct.ThrowIfCancellationRequested();
        if (!_automaticChecksEnabled() || Volatile.Read(ref _attempts) != 0) return null;
        await _startupDelay(ct);
        ct.ThrowIfCancellationRequested();
        // Read the current preferences after the delay: Settings may have been changed or reset.
        if (!_automaticChecksEnabled() || Volatile.Read(ref _attempts) != 0) return null;
        return await TryCheckAsync(automatic: true, ct);
    }

    public Task<UpdateCheckResult?> CheckNowAsync(CancellationToken ct) => TryCheckAsync(automatic: false, ct);

    private async Task<UpdateCheckResult?> TryCheckAsync(bool automatic, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var acquired = false;
        try
        {
            lock (_gate)
            {
                // A manual request can finish after startup's earlier attempt check.
                // Recheck while acquiring the shared guard, before announcing another request.
                if (_checking != 0 || automatic && Volatile.Read(ref _attempts) != 0) return null;
                Volatile.Write(ref _checking, 1);
                Interlocked.Increment(ref _attempts);
                acquired = true;
                CheckingChanged?.Invoke(true);
            }
            if (automatic && !_automaticChecksEnabled()) return null;
            var includePrereleases = _includePrereleases();
            var result = await _check(ct, includePrereleases);
            ct.ThrowIfCancellationRequested();
            // A result from the old channel must never offer a beta after the preference changed.
            if (includePrereleases != _includePrereleases() || automatic && !_automaticChecksEnabled()) return null;
            return result;
        }
        finally
        {
            if (acquired)
            {
                lock (_gate)
                {
                    Volatile.Write(ref _checking, 0);
                    CheckingChanged?.Invoke(false);
                }
            }
        }
    }
}
