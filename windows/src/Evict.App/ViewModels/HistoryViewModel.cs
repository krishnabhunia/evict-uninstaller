using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(UninstallHistoryEntry e) => Entry = e;
    public UninstallHistoryEntry Entry { get; }
    public string Name => Entry.ProgramName;
    public string Publisher => Entry.Publisher ?? "";
    public string Version => Entry.Version ?? "";
    public string When => Entry.Timestamp.ToString("dd MMM yyyy HH:mm");
    public string MethodText => Entry.Method switch
    {
        UninstallMethod.Standard => "Standard",
        UninstallMethod.Quiet => "Silent",
        UninstallMethod.Force => "Force",
        UninstallMethod.InstallLog => "Install log",
        UninstallMethod.RegistryEntryOnly => "Entry only",
        _ => Entry.Method.ToString(),
    };
    public string ResultText => Entry.Succeeded ? "Succeeded" : "Check";
    public bool Succeeded => Entry.Succeeded;
    public string LeftoversText => Entry.LeftoversFound == 0 ? "—" : $"{Entry.LeftoversRemoved}/{Entry.LeftoversFound}";
    public string ReclaimedText => Entry.BytesReclaimed > 0 ? SizeFormatter.Format(Entry.BytesReclaimed) : "—";
    public string Notes => Entry.Notes ?? "";
    public bool HasLocation => !string.IsNullOrEmpty(Entry.InstallLocation);
}

public sealed partial class HistoryViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Reload();
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();
    [ObservableProperty] private HistoryItemViewModel? _selectedItem;
    [ObservableProperty] private string _searchText = "";

    public bool HasItems => Items.Count > 0;
    public string Summary
    {
        get
        {
            var all = _services.History.Entries;
            return all.Count == 0 ? "No uninstalls recorded yet." : $"{all.Count} operations · {SizeFormatter.Format(all.Sum(e => e.BytesReclaimed))} reclaimed in total";
        }
    }

    public void OnActivated() => Reload();

    partial void OnSearchTextChanged(string value) => Reload();

    public void Reload()
    {
        Items.Clear();
        foreach (var e in _services.History.Entries)
        {
            if (!string.IsNullOrWhiteSpace(SearchText) && !e.ProgramName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
                && !(e.Publisher?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            Items.Add(new HistoryItemViewModel(e));
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void RescanLeftovers(HistoryItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        var vm = new ForceUninstallViewModel(_services, t.Name, t.Entry.InstallLocation);
        new ForceUninstallWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        if (vm.AnythingChanged) Reload();
    }

    [RelayCommand]
    private void Remove(HistoryItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        _services.History.Remove(t.Entry.Id);
        Reload();
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (!Dialogs.Confirm("Clear the entire uninstall history?")) return;
        _services.History.Clear();
        Reload();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "evict-history.csv", Filter = "CSV file|*.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Program,Publisher,Version,Method,Succeeded,ExitCode,LeftoversFound,LeftoversRemoved,BytesReclaimed,Notes");
        foreach (var e in _services.History.Entries)
            sb.AppendLine(string.Join(",", Csv(e.Timestamp.ToString("s")), Csv(e.ProgramName), Csv(e.Publisher), Csv(e.Version), Csv(e.Method.ToString()), e.Succeeded, e.ExitCode?.ToString() ?? "", e.LeftoversFound, e.LeftoversRemoved, e.BytesReclaimed, Csv(e.Notes)));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
