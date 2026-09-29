using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed partial class AppxItemViewModel : ObservableObject
{
    public AppxItemViewModel(AppxPackageInfo info) => Info = info;
    public AppxPackageInfo Info { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    public string Name => Info.DisplayName ?? Info.Name;
    public string PackageName => Info.Name;
    public string Publisher => Info.PublisherDisplayName ?? Info.Publisher ?? "—";
    public string Version => Info.Version ?? "—";
    public long SizeSort => Info.SizeBytes ?? -1;
    public string SizeText => SizeFormatter.Format(Info.SizeBytes);
    public string TypeText => Info.IsFramework ? "Framework" : Info.IsSystem ? "System" : string.Equals(Info.SignatureKind, "Store", StringComparison.OrdinalIgnoreCase) ? "Store" : Info.SignatureKind ?? "App";
    public bool IsBloat => Info.IsKnownBloatware;
    public bool IsRemovable => !Info.NonRemovable;
    public string Tag => IsBloat ? "Bloatware" : "";
    public string InstallDateText => Info.InstallDate is { } d ? d.ToString("dd MMM yyyy") : "—";

    public bool Matches(string s) => string.IsNullOrWhiteSpace(s) || Name.Contains(s, StringComparison.OrdinalIgnoreCase)
        || PackageName.Contains(s, StringComparison.OrdinalIgnoreCase) || Publisher.Contains(s, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class WindowsAppsViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private bool _loaded;

    public WindowsAppsViewModel(AppServices services)
    {
        _services = services;
        Items = new ObservableCollection<AppxItemViewModel>();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = Filter;
        View.SortDescriptions.Add(new SortDescription(nameof(AppxItemViewModel.Name), ListSortDirection.Ascending));
        _showFrameworks = !services.Settings.Current.HideFrameworkAppx;
        _showSystem = services.Settings.Current.ShowSystemAppx;
    }

    public ObservableCollection<AppxItemViewModel> Items { get; }
    public ICollectionView View { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showFrameworks;
    [ObservableProperty] private bool _showSystem;
    [ObservableProperty] private bool _onlyBloatware;
    [ObservableProperty] private bool _allUsers = ElevationHelper.IsElevated;
    [ObservableProperty] private bool _alsoRemoveProvisioned = ElevationHelper.IsElevated;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool? _allSelected = false;
    [ObservableProperty] private AppxItemViewModel? _selectedItem;
    [ObservableProperty] private string? _error;

    public bool IsElevated => ElevationHelper.IsElevated;
    public int VisibleCount => View.Cast<object>().Count();
    public int BloatCount => Items.Count(i => i.IsBloat);
    public string Summary => $"{VisibleCount} apps shown · {Items.Count} total · {BloatCount} flagged as bloatware";

    public void OnActivated() { if (!_loaded) _ = RefreshAsync(); }

    partial void OnSearchTextChanged(string value) => Apply();
    partial void OnShowFrameworksChanged(bool value) { _services.Settings.Current.HideFrameworkAppx = !value; _services.Settings.Save(); Apply(); }
    partial void OnShowSystemChanged(bool value) { _services.Settings.Current.ShowSystemAppx = value; _services.Settings.Save(); Apply(); }
    partial void OnOnlyBloatwareChanged(bool value) => Apply();
    partial void OnIsBusyChanged(bool value) => RemoveSelectedCommand.NotifyCanExecuteChanged();

    private void Apply()
    {
        View.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(Summary));
        UpdateSelection();
    }

    private bool Filter(object o)
    {
        if (o is not AppxItemViewModel a) return false;
        if (!a.Matches(SearchText)) return false;
        if (!ShowFrameworks && a.Info.IsFramework) return false;
        if (!ShowSystem && a.Info.IsSystem && !a.IsBloat) return false;
        if (OnlyBloatware && !a.IsBloat) return false;
        return true;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Error = null;
        StatusText = "Querying Windows apps (PowerShell)…";
        try
        {
            var progress = new Progress<ProgressReport>(r => StatusText = r.Message);
            var (packages, error) = await _services.Appx.GetPackagesAsync(AllUsers, progress, CancellationToken.None);
            foreach (var i in Items) i.PropertyChanged -= ItemChanged;
            Items.Clear();
            foreach (var p in packages)
            {
                var vm = new AppxItemViewModel(p);
                vm.PropertyChanged += ItemChanged;
                Items.Add(vm);
            }
            if (packages.Count == 0 && error != null) Error = error;
            _loaded = true;
            Apply();
            OnPropertyChanged(nameof(BloatCount));
            StatusText = "";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private void ItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppxItemViewModel.IsSelected)) UpdateSelection();
    }

    private bool _sync;
    private void UpdateSelection()
    {
        if (_sync) return;
        _sync = true;
        try
        {
            SelectedCount = Items.Count(i => i.IsSelected);
            var visible = View.Cast<AppxItemViewModel>().ToList();
            int sel = visible.Count(v => v.IsSelected);
            AllSelected = visible.Count == 0 || sel == 0 ? false : sel == visible.Count ? true : null;
        }
        finally { _sync = false; }
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnAllSelectedChanged(bool? value)
    {
        if (_sync || value is null) return;
        _sync = true;
        try { foreach (var v in View.Cast<AppxItemViewModel>()) v.IsSelected = value.Value && v.IsRemovable; }
        finally { _sync = false; }
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectBloatware()
    {
        _sync = true;
        try { foreach (var v in View.Cast<AppxItemViewModel>()) v.IsSelected = v.IsBloat && v.IsRemovable; }
        finally { _sync = false; }
        UpdateSelection();
    }

    private bool CanRemove() => SelectedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;
        var names = string.Join("\n", targets.Take(12).Select(t => "  • " + t.Name)) + (targets.Count > 12 ? $"\n  … and {targets.Count - 12} more" : "");
        if (!Dialogs.Confirm($"Remove {targets.Count} Windows app(s)?\n\n{names}\n\nStore apps can be reinstalled from the Microsoft Store." +
                             (AlsoRemoveProvisioned && IsElevated ? "\n\nProvisioned copies will also be removed so they do not return for new user accounts." : ""), destructive: true))
            return;

        IsBusy = true;
        int ok = 0, fail = 0;
        try
        {
            foreach (var t in targets)
            {
                StatusText = $"Removing {t.Name}…";
                t.Status = "Removing…";
                var (success, msg) = await _services.Appx.RemoveAsync(t.Info, AllUsers, CancellationToken.None);
                if (success && AlsoRemoveProvisioned && IsElevated && !t.Info.IsFramework)
                {
                    var (_, pmsg) = await _services.Appx.RemoveProvisionedAsync(t.Info, CancellationToken.None);
                    msg += " " + pmsg;
                }
                t.Status = msg;
                if (success) ok++; else fail++;
                _services.History.Add(new UninstallHistoryEntry
                {
                    ProgramName = t.Name, Publisher = t.Publisher, Version = t.Version, Method = UninstallMethod.Standard,
                    Succeeded = success, Notes = "Windows app: " + msg, InstallLocation = t.Info.InstallLocation, BytesReclaimed = success ? t.Info.SizeBytes ?? 0 : 0,
                });
            }
            StatusText = $"Removed {ok} app(s)" + (fail > 0 ? $", {fail} failed." : ".");
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
        StatusText = $"Removed {ok} app(s)" + (fail > 0 ? $", {fail} failed – see the Status column." : ".");
    }

    [RelayCommand]
    private void OpenInstallFolder(AppxItemViewModel? item) => Dialogs.OpenFolder((item ?? SelectedItem)?.Info.InstallLocation);

    [RelayCommand]
    private void OpenInStore(AppxItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t?.Info.PackageFamilyName is { } pfn) Dialogs.OpenUrl("ms-windows-store://pdp/?PFN=" + pfn);
    }

    [RelayCommand]
    private void CopyPackageName(AppxItemViewModel? item) => Dialogs.CopyToClipboard((item ?? SelectedItem)?.Info.PackageFullName);
}
