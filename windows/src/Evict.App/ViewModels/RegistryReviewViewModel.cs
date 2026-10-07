using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

/// <summary>
/// Small dialog: a checkable list of registry entries → back up to .reg → delete → optional undo.
/// Used by Install Monitor ("Clean registry of this installation").
/// </summary>
public sealed partial class RegistryReviewViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly string _backupLabel;
    private UninstallHistoryEntry? _historyEntry;

    public RegistryReviewViewModel(AppServices services, string title, string subtitle, IEnumerable<LeftoverItem> items, string backupLabel)
    {
        _services = services;
        _backupLabel = backupLabel;
        Title = title;
        Subtitle = subtitle;
        Review = new LeftoverReviewViewModel();
        Review.Load(items, selectLowConfidence: false);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public LeftoverReviewViewModel Review { get; }
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private CleanupStep _step = CleanupStep.Review;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _backupFile;

    public bool AnythingChanged { get; private set; }
    public event Action? RequestClose;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        var selected = Review.SelectedLeftovers;
        if (selected.Count == 0) return;
        if (!Dialogs.Confirm($"Remove {selected.Count} registry entr{(selected.Count == 1 ? "y" : "ies")}?\n\nThey are saved to a .reg backup first and can be put back with \"Undo\".", destructive: true)) return;
        Step = CleanupStep.Cleaning;
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        var result = await _services.Cleaner.CleanAsync(selected, new CleanupOptions { BackupLabel = _backupLabel, SendToRecycleBin = false }, progress, CancellationToken.None);
        BackupFile = result.RegistryBackupFile;
        foreach (var (item, error) in result.Errors) Errors.Add($"{item.Path}: {error}");
        AnythingChanged = result.Removed > 0;
        StatusText = $"Removed {result.Removed} of {selected.Count} entr{(selected.Count == 1 ? "y" : "ies")}" + (result.Failed > 0 ? $" – {result.Failed} failed." : ".");
        _historyEntry = new UninstallHistoryEntry
        {
            ProgramName = _backupLabel, Method = UninstallMethod.Force, Succeeded = result.Failed == 0,
            LeftoversFound = selected.Count, LeftoversRemoved = result.Removed,
            Notes = StatusText + (BackupFile != null ? $" Backup: {BackupFile}" : ""),
        };
        _services.History.Upsert(_historyEntry);
        Step = CleanupStep.Done;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (BackupFile is null) return;
        if (!Dialogs.Confirm("Put back every registry entry that was just removed?")) return;
        var (ok, message) = await RegistryBackupService.RestoreAsync(BackupFile);
        StatusText = message;
        if (ok)
        {
            BackupFile = null;
            if (_historyEntry is { } previous)
            {
                _historyEntry = new UninstallHistoryEntry
                {
                    Id = previous.Id, Timestamp = previous.Timestamp, ProgramName = previous.ProgramName,
                    Method = previous.Method, Succeeded = false, LeftoversFound = previous.LeftoversFound,
                    LeftoversRemoved = 0, Notes = "Registry cleanup was undone: " + message,
                };
                _services.History.Upsert(_historyEntry);
            }
        }
        if (ok) Dialogs.Info(message); else Dialogs.Error(message);
    }

    [RelayCommand] private void Close() { if (Step != CleanupStep.Cleaning) RequestClose?.Invoke(); }
}
