using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
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
    public string ReclaimedText => Entry.BytesReclaimed > 0 ? SizeFormatter.Format(Entry.BytesReclaimed) : "-";
    public string SpaceText => Entry.BytesRemoved > 0
        ? $"{SizeFormatter.Format(Entry.BytesRemoved)} / {SizeFormatter.Format(Entry.BytesReclaimed)}"
        : Entry.BytesReclaimed > 0 ? SizeFormatter.Format(Entry.BytesReclaimed) + " (recorded)" : "-";
    public string Notes => Entry.Notes ?? "";
    public bool HasLocation => !string.IsNullOrEmpty(Entry.InstallLocation);
}

/// <summary>One row of the RD1 History timeline: an uninstall or a program update.</summary>
public sealed class TimelineItemViewModel
{
    public TimelineItemViewModel(ActivityItem item, UninstallHistoryEntry? uninstall, DateTime nowLocal)
    {
        Item = item;
        Uninstall = uninstall is null || item.Kind != ActivityKind.Uninstall ? null : new HistoryItemViewModel(uninstall);
        DayLabel = SoftwareUpdateHistory.DayLabel(item.WhenLocal, nowLocal);
    }

    public ActivityItem Item { get; }
    public HistoryItemViewModel? Uninstall { get; }
    public string DayLabel { get; }
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string Time => Item.WhenLocal.ToString("HH:mm");
    public bool Succeeded => Item.Succeeded;
    public bool IsUninstall => Item.Kind == ActivityKind.Uninstall;
    public bool IsUpdate => Item.Kind == ActivityKind.Update;
    public string Glyph => IsUpdate ? "\uE895" : "\uE74D";
    public string KindText => IsUpdate ? "Update" : "Uninstall";
    public string Notes => Uninstall?.Notes ?? "";
    public bool HasNotes => Notes.Length > 0;
}

public sealed partial class HistoryViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public HistoryViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TimelineItemViewModel.DayLabel)));
        _services.UpdateHistory.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Reload();
            else dispatcher.BeginInvoke(Reload);
        };
        Reload();
    }

    public ObservableCollection<TimelineItemViewModel> Items { get; } = new();
    /// <summary>Items grouped by day ("Today · Mon 26 Oct", "Yesterday · …").</summary>
    public ICollectionView View { get; }
    [ObservableProperty] private TimelineItemViewModel? _selectedItem;
    [ObservableProperty] private string _searchText = "";
    /// <summary>All / Uninstalls / Updates chips.</summary>
    [ObservableProperty] private string _filter = "All";

    public bool HasItems => Items.Count > 0;
    public string Summary
    {
        get
        {
            var all = _services.History.Entries;
            var updates = _services.UpdateHistory.Entries.Count(u => u.IsFinished);
            if (all.Count == 0 && updates == 0) return "Nothing recorded yet.";
            return $"{all.Count} uninstalls · {updates} updates in the last {SoftwareUpdateHistory.KeepDays} days · {SizeFormatter.Format(all.Sum(e => e.BytesRemoved))} removed";
        }
    }

    public void OnActivated() => Reload();

    partial void OnSearchTextChanged(string value) => Reload();
    partial void OnFilterChanged(string value) => Reload();

    /// <summary>The newest few things Evict did (Home → Recently).</summary>
    public IReadOnlyList<TimelineItemViewModel> Recent(int count)
    {
        var now = DateTime.Now;
        var byId = UninstallsById();
        return ActivityTimeline.Build(_services.History.Entries, _services.UpdateHistory.Entries).Take(count)
            .Select(i => new TimelineItemViewModel(i, byId.GetValueOrDefault(i.Id), now)).ToList();
    }

    public void Reload()
    {
        Items.Clear();
        ActivityKind? only = Filter switch { "Uninstalls" => ActivityKind.Uninstall, "Updates" => ActivityKind.Update, _ => null };
        var now = DateTime.Now;
        var byId = UninstallsById();
        foreach (var item in ActivityTimeline.Build(_services.History.Entries, _services.UpdateHistory.Entries, only, SearchText))
            Items.Add(new TimelineItemViewModel(item, byId.GetValueOrDefault(item.Id), now));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
        RecentChanged?.Invoke();
    }

    private Dictionary<Guid, UninstallHistoryEntry> UninstallsById() =>
        _services.History.Entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>Raised after every reload so Home can refresh its "Recently" list.</summary>
    public event Action? RecentChanged;

    [RelayCommand]
    private void RescanLeftovers(TimelineItemViewModel? item)
    {
        var t = (item ?? SelectedItem)?.Uninstall;
        if (t is null) return;
        var vm = new ForceUninstallViewModel(_services, t.Name, t.Entry.InstallLocation);
        new ForceUninstallWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        if (vm.AnythingChanged) Reload();
    }

    [RelayCommand]
    private void Remove(TimelineItemViewModel? item)
    {
        var t = (item ?? SelectedItem)?.Uninstall;
        if (t is null) return;
        _services.History.Remove(t.Entry.Id);
        Reload();
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (!Dialogs.Confirm("Clear the uninstall history? (Program updates are kept for 7 days and can be cleared in Updates → History.)")) return;
        _services.History.Clear();
        Reload();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "evict-history.csv", Filter = "CSV file|*.csv" };
        if (dlg.ShowDialog() != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp,Program,Publisher,Version,Method,Succeeded,ExitCode,LeftoversFound,LeftoversRemoved,BytesReclaimed,Notes,BytesRemoved");
        foreach (var e in _services.History.Entries)
            sb.AppendLine(string.Join(",", Csv(e.Timestamp.ToString("s")), Csv(e.ProgramName), Csv(e.Publisher), Csv(e.Version), Csv(e.Method.ToString()), e.Succeeded, e.ExitCode?.ToString() ?? "", e.LeftoversFound, e.LeftoversRemoved, e.BytesReclaimed, Csv(e.Notes), e.BytesRemoved));
        File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    }
}
