using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum SelfCleanupStep { Choose, Working, Done }

public sealed partial class SelfCleanupOptionViewModel : ObservableObject
{
    public SelfCleanupOptionViewModel(SelfCleanupEntry entry)
    {
        Entry = entry;
        IsAvailable = entry.Count > 0 && (!entry.NeedsAdmin || ElevationHelper.IsElevated);
        _isChecked = IsAvailable && entry.Recommended;
    }

    public SelfCleanupEntry Entry { get; }
    [ObservableProperty] private bool _isChecked;
    public bool IsAvailable { get; }
    public string Title => Entry.Title;
    public string Detail => Entry.Detail;
    public string AmountText => Entry.Count == 0
        ? "nothing found"
        : Entry.SizeBytes > 0 ? SizeFormatter.Format(Entry.SizeBytes) : $"{Entry.Count} item(s)";
    public bool NeedsElevation => Entry.NeedsAdmin && !ElevationHelper.IsElevated && Entry.Count > 0;
}

/// <summary>
/// "Remove Evict's leftovers": shown by the uninstaller (<c>Evict.exe --self-cleanup ask</c>) and by a portable
/// copy's Settings. The Windows integration is always removed; the optional parts are ticked by the user.
/// </summary>
public sealed partial class SelfCleanupViewModel : ObservableObject
{
    public SelfCleanupViewModel(bool portable)
    {
        IsPortable = portable;
        foreach (var e in SelfCleanupService.Describe()) Options.Add(new SelfCleanupOptionViewModel(e));
    }

    public bool IsPortable { get; }
    public ObservableCollection<SelfCleanupOptionViewModel> Options { get; } = new();
    public IReadOnlyList<string> AlwaysRemoved => SelfCleanupService.AlwaysRemoved;
    public ObservableCollection<string> Failures { get; } = new();

    [ObservableProperty] private SelfCleanupStep _step = SelfCleanupStep.Choose;
    [ObservableProperty] private string _resultText = "";

    public string Subtitle => IsPortable
        ? "Evict has closed. Choose which of its files and registry entries to remove from this PC. Evict.exe itself is not deleted – remove it afterwards."
        : "Evict is being uninstalled. Its program files are removed by the uninstaller; choose which of the data it created should go too.";
    public string ExeFolder => UpdateService.ExeDirectory;
    public event Action? RequestClose;

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var parts = Options.Where(o => o.IsChecked && o.IsAvailable).Aggregate(SelfCleanupParts.None, (acc, o) => acc | o.Entry.Part);
        await RunAsync(parts);
    }

    [RelayCommand]
    private async Task KeepOptionalAsync() => await RunAsync(SelfCleanupParts.None);

    private async Task RunAsync(SelfCleanupParts parts)
    {
        if (Step != SelfCleanupStep.Choose) return;
        Step = SelfCleanupStep.Working;
        var report = await Task.Run(() => SelfCleanupService.Run(parts, UpdateService.ExeDirectory));
        foreach (var f in report.Failed) Failures.Add(f);
        ResultText = report.Failed.Count == 0
            ? $"Removed {report.Removed.Count} item(s)."
            : $"Removed {report.Removed.Count} item(s); {report.Failed.Count} could not be removed (listed below).";
        Step = SelfCleanupStep.Done;
    }

    /// <summary>Closing the window before choosing still removes the integration (registry, task, menus).</summary>
    public void OnClosing()
    {
        if (Step == SelfCleanupStep.Choose)
        {
            Step = SelfCleanupStep.Working;
            try { SelfCleanupService.Run(SelfCleanupParts.None, UpdateService.ExeDirectory); } catch { /* best effort */ }
        }
    }

    [RelayCommand] private void OpenExeFolder() => Dialogs.OpenFolder(ExeFolder);
    [RelayCommand] private void Close() { if (Step != SelfCleanupStep.Working) RequestClose?.Invoke(); }
}
