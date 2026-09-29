using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum ResidualStep { Options, Scanning, Review, Cleaning, Done }

/// <summary>Residual Cleaner dialog: options → scan → review (shared list) → clean → summary.</summary>
public sealed partial class ResidualViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly IReadOnlyList<InstalledProgram> _installed;
    private CancellationTokenSource? _cts;

    public ResidualViewModel(AppServices services, IReadOnlyList<InstalledProgram> installed)
    {
        _services = services;
        _installed = installed;
        _sendToRecycleBin = services.Settings.Current.SendToRecycleBin;
        Review = new LeftoverReviewViewModel();
    }

    public LeftoverReviewViewModel Review { get; }
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private ResidualStep _step = ResidualStep.Options;
    [ObservableProperty] private bool _fromHistory = true;
    [ObservableProperty] private bool _brokenEntries = true;
    [ObservableProperty] private bool _unmatchedFolders = true;
    [ObservableProperty] private int _minAgeDays = 30;
    [ObservableProperty] private bool _sendToRecycleBin;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _removed;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private long _bytesReclaimed;

    public bool AnythingChanged { get; private set; }
    public string BytesReclaimedText => SizeFormatter.Format(BytesReclaimed);
    public int HistoryCandidates => _services.History.Entries.Count(h => h.Method is UninstallMethod.Standard or UninstallMethod.Quiet or UninstallMethod.Force);
    public int BrokenCount => _installed.Count(p => p.IsBrokenEntry);
    public string HistoryHint => $"{HistoryCandidates} uninstall(s) recorded in History will be re-checked.";
    public string BrokenHint => BrokenCount == 0 ? "No broken Programs & Features entries right now." : $"{BrokenCount} broken entr{(BrokenCount == 1 ? "y" : "ies")} found in the program list.";
    public event Action? RequestClose;

    [RelayCommand]
    private async Task ScanAsync()
    {
        _cts = new CancellationTokenSource();
        Step = ResidualStep.Scanning;
        Progress = 0;
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var installed = _installed.Count > 0 ? _installed : await Task.Run(() => _services.Programs.Enumerate(new ProgramsQueryOptions()));
            var result = await _services.Residual.ScanAsync(installed, _services.History.Entries,
                new ResidualScanOptions
                {
                    FromHistory = FromHistory, BrokenEntries = BrokenEntries, UnmatchedFolders = UnmatchedFolders,
                    MinAgeDays = Math.Clamp(MinAgeDays, 1, 3650), ScanAllUserProfiles = _services.Settings.Current.ScanAllUserProfiles,
                }, progress, _cts.Token);
            Review.Load(result.Items, selectLowConfidence: false);
            Step = ResidualStep.Review;
        }
        catch (OperationCanceledException) { Step = ResidualStep.Options; }
        catch (Exception ex)
        {
            Dialogs.Error("Residual scan failed: " + ex.Message);
            Step = ResidualStep.Options;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        var items = Review.SelectedLeftovers;
        if (items.Count == 0) return;
        int low = items.Count(i => i.Confidence == LeftoverConfidence.Low);
        var msg = $"Remove {items.Count} item(s)?";
        if (low > 0) msg += $"\n\n⚠ {low} of them are 'Review' items – folders no installed program claims. Make sure none of them holds data you still need (portable apps, game saves, project files).";
        if (!Dialogs.Confirm(msg, destructive: true)) return;

        Step = ResidualStep.Cleaning;
        Progress = 0;
        Errors.Clear();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        var result = await _services.Cleaner.CleanAsync(items, new CleanupOptions { SendToRecycleBin = SendToRecycleBin }, progress, CancellationToken.None);
        Removed = result.Removed; Failed = result.Failed; BytesReclaimed = result.BytesReclaimed;
        OnPropertyChanged(nameof(BytesReclaimedText));
        foreach (var (item, error) in result.Errors) Errors.Add($"{item.Path}: {error}");
        AnythingChanged = true;
        _services.History.Add(new UninstallHistoryEntry
        {
            ProgramName = "Residual Cleaner", Method = UninstallMethod.Force, Succeeded = result.Failed == 0,
            LeftoversFound = Review.TotalCount, LeftoversRemoved = result.Removed, BytesReclaimed = result.BytesReclaimed,
            Notes = $"Removed leftovers of previously uninstalled programs ({items.Count} selected)",
        });
        Step = ResidualStep.Done;
    }

    [RelayCommand] private void Back() => Step = ResidualStep.Options;
    [RelayCommand] private void Cancel() { if (Step == ResidualStep.Scanning) _cts?.Cancel(); else RequestClose?.Invoke(); }
    [RelayCommand] private void Close() => RequestClose?.Invoke();
}
