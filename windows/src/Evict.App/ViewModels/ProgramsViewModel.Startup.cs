namespace Evict.App.ViewModels;

public sealed partial class ProgramsViewModel
{
    /// <summary>Only the first registry inventory is required at startup, not folder-size enrichment.</summary>
    public async Task PrepareStartupInventoryAsync(CancellationToken ct)
    {
        try
        {
            await EnsureLoadedAsync().WaitAsync(TimeSpan.FromSeconds(60), ct);
            ct.ThrowIfCancellationRequested();
            // The legacy first-load signal also completes on errors. Do not mistake that for readiness.
            if (!_loadedOnce)
                throw new InvalidOperationException("The installed-program list could not be loaded.", _lastLoadException);
        }
        catch
        {
            _loadCts?.Cancel();
            throw;
        }
    }
}
