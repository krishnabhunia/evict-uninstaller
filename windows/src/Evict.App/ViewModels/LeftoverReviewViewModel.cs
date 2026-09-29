using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed partial class LeftoverItemViewModel : ObservableObject
{
    public LeftoverItemViewModel(LeftoverItem item, bool selected)
    {
        Item = item;
        _isSelected = selected;
    }

    public LeftoverItem Item { get; }
    [ObservableProperty] private bool _isSelected;

    public string Path => Item.Path;
    public string KindText => Item.Kind switch
    {
        LeftoverKind.Folder => "Folder",
        LeftoverKind.File => "File",
        LeftoverKind.Shortcut => "Shortcut",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.StartupEntry => "Startup entry",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Scheduled task",
        _ => Item.Kind.ToString(),
    };
    public string KindGlyph => Item.Kind switch
    {
        LeftoverKind.Folder => "",
        LeftoverKind.File => "",
        LeftoverKind.Shortcut => "",
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => "",
        LeftoverKind.StartupEntry => "",
        LeftoverKind.Service => "",
        LeftoverKind.ScheduledTask => "",
        _ => "",
    };
    public string GroupName => Item.ProgramName ?? "Leftovers";
    public string SizeText => Item.IsFileSystem ? SizeFormatter.Format(Item.SizeBytes) : "";
    public string ConfidenceText => Item.Confidence switch
    {
        LeftoverConfidence.High => "Safe",
        LeftoverConfidence.Medium => "Likely",
        _ => "Review",
    };
    public string Detail => Item.Detail ?? "";
    public bool IsHigh => Item.Confidence == LeftoverConfidence.High;
    public bool IsMedium => Item.Confidence == LeftoverConfidence.Medium;
    public bool IsLow => Item.Confidence == LeftoverConfidence.Low;
    public string Tooltip => $"{KindText}\n{Path}\n{Detail}";

    [RelayCommand]
    private void Open()
    {
        if (Item.IsFileSystem) Dialogs.OpenFolder(Item.Kind == LeftoverKind.Folder ? Item.Path : Item.Path);
        else if (Item.IsRegistry && Item.Hive != null && Item.SubKey != null)
        {
            var hive = Item.Hive == Microsoft.Win32.RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
            var sub = Item.SubKey;
            if (Item.RegView == Microsoft.Win32.RegistryView.Registry32 && sub.StartsWith("SOFTWARE\\", StringComparison.OrdinalIgnoreCase))
                sub = "SOFTWARE\\WOW6432Node\\" + sub[9..];
            Dialogs.OpenRegistryKey(hive + "\\" + sub);
        }
    }
}

/// <summary>Shared by the uninstall wizard and Force Uninstall: a grouped, checkable list of leftovers.</summary>
public sealed partial class LeftoverReviewViewModel : ObservableObject
{
    public LeftoverReviewViewModel()
    {
        Items = new ObservableCollection<LeftoverItemViewModel>();
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.GroupName)));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.GroupName), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.KindText), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(LeftoverItemViewModel.Path), ListSortDirection.Ascending));
    }

    public ObservableCollection<LeftoverItemViewModel> Items { get; }
    public ICollectionView View { get; }

    [ObservableProperty] private bool? _allSelected;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private bool _groupByProgram = true;

    public int TotalCount => Items.Count;
    public long TotalBytes => Items.Sum(i => i.Item.SizeBytes);
    public int FileCount => Items.Count(i => i.Item.IsFileSystem);
    public int RegistryCount => Items.Count(i => i.Item.IsRegistry);
    public int OtherCount => Items.Count(i => !i.Item.IsFileSystem && !i.Item.IsRegistry);
    public bool HasItems => Items.Count > 0;
    public bool HasLowConfidence => Items.Any(i => i.IsLow);
    public string SelectedSummary => SelectedCount == 0 ? "Nothing selected" : $"{SelectedCount} of {TotalCount} selected · {SizeFormatter.Format(SelectedBytes)}";

    public void Load(IEnumerable<LeftoverItem> items, bool selectLowConfidence = false)
    {
        foreach (var i in Items) i.PropertyChanged -= ItemChanged;
        Items.Clear();
        foreach (var it in items)
        {
            var vm = new LeftoverItemViewModel(it, selectLowConfidence || it.Confidence != LeftoverConfidence.Low);
            vm.PropertyChanged += ItemChanged;
            Items.Add(vm);
        }
        RaiseAll();
    }

    public IReadOnlyList<LeftoverItem> SelectedLeftovers => Items.Where(i => i.IsSelected).Select(i => i.Item).ToList();

    private bool _sync;
    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LeftoverItemViewModel.IsSelected)) RaiseAll();
    }

    private void RaiseAll()
    {
        if (_sync) return;
        _sync = true;
        try
        {
            SelectedCount = Items.Count(i => i.IsSelected);
            SelectedBytes = Items.Where(i => i.IsSelected).Sum(i => i.Item.SizeBytes);
            AllSelected = Items.Count == 0 ? false : SelectedCount == 0 ? false : SelectedCount == Items.Count ? true : null;
        }
        finally { _sync = false; }
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TotalBytes));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(RegistryCount));
        OnPropertyChanged(nameof(OtherCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasLowConfidence));
        OnPropertyChanged(nameof(SelectedSummary));
    }

    partial void OnAllSelectedChanged(bool? value)
    {
        if (_sync || value is null) return;
        _sync = true;
        try { foreach (var i in Items) i.IsSelected = value.Value; }
        finally { _sync = false; }
        RaiseAll();
    }

    partial void OnGroupByProgramChanged(bool value)
    {
        View.GroupDescriptions.Clear();
        if (value) View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LeftoverItemViewModel.GroupName)));
    }

    [RelayCommand] private void SelectAll() { AllSelected = true; }
    [RelayCommand] private void SelectNone() { AllSelected = false; }
    [RelayCommand]
    private void SelectSafeOnly()
    {
        _sync = true;
        try { foreach (var i in Items) i.IsSelected = !i.IsLow; }
        finally { _sync = false; }
        RaiseAll();
    }
}
