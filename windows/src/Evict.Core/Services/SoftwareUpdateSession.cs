using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Records every selected attempt, including cancellation while waiting for a slot.</summary>
public sealed class SoftwareUpdateSession
{
    private readonly SoftwareUpdateHistoryStore _history;
    private readonly Func<UpgradablePackage, CancellationToken, Action<string>?, bool, Task<SoftwareUpgradeResult>> _upgrade;

    public SoftwareUpdateSession(SoftwareUpdateHistoryStore history, WingetService winget)
        : this(history, (package, ct, output, unknown) => winget.UpgradeWithResultAsync(package, ct, output, unknown)) { }

    internal SoftwareUpdateSession(SoftwareUpdateHistoryStore history,
        Func<UpgradablePackage, CancellationToken, Action<string>?, bool, Task<SoftwareUpgradeResult>> upgrade)
    { _history = history; _upgrade = upgrade; }

    public async Task<IReadOnlyList<SoftwareUpdateAttempt>> RunAsync(IReadOnlyList<UpgradablePackage> packages,
        int parallel, bool includeUnknown, CancellationToken ct,
        Action<SoftwareUpdateAttempt>? onChanged = null, Action<UpgradablePackage, string>? onOutput = null)
    {
        var queued = packages.Select(p => (Package: p, Id: _history.Begin(p))).ToArray();
        using var slots = new SemaphoreSlim(Math.Clamp(parallel, 1, 6));
        return await Task.WhenAll(queued.Select(async item =>
        {
            bool entered = false, started = false;
            void Report()
            {
                var record = _history.Snapshot().First(r => r.Id == item.Id);
                try { onChanged?.Invoke(record); } catch { /* presentation does not change installation */ }
            }
            Report();
            SoftwareUpgradeResult result;
            try
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                entered = true;
                ct.ThrowIfCancellationRequested();
                started = true;
                _history.MarkRunning(item.Id);
                Report();
                result = await _upgrade(item.Package, ct, line =>
                {
                    _history.AppendOutput(item.Id, line);
                    try { onOutput?.Invoke(item.Package, line); } catch { /* output observer */ }
                }, includeUnknown).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = started
                    ? new(SoftwareUpdateOutcome.Interrupted, "Cancellation was requested while an installer was running. Its final result is unknown; check the installed version before trying again.")
                    : new(SoftwareUpdateOutcome.Cancelled, "Cancelled before the installer started.");
            }
            catch (Exception ex)
            {
                result = new(SoftwareUpdateOutcome.Interrupted, "The installer result could not be confirmed. Check the installed version before trying again. " + ex.Message);
            }
            finally { if (entered) slots.Release(); }
            var completed = _history.Complete(item.Id, result)
                ?? throw new InvalidOperationException("The active update attempt was unexpectedly removed.");
            // A user may clear completed history immediately. Keep this detached result for the session.
            try { onChanged?.Invoke(completed); } catch { /* presentation observer */ }
            return completed;
        })).ConfigureAwait(false);
    }
}
