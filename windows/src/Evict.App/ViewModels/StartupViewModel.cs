using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly StartupViewModel _owner;
    public StartupItemViewModel(StartupItem item, StartupViewModel owner)
    {
        Item = item;
        _owner = owner;
        _enabled = item.Enabled;
    }

    public StartupItem Item { get; }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "";

    public string Name => Item.Name;
    public string Command => Item.Command;
    public string LocationText => Item.LocationText;
    public bool RequiresAdmin => Item.RequiresAdmin;
    public bool TargetMissing => !Item.TargetExists;
    public bool CanToggle => Item.Location is not (StartupLocation.UserRunOnce or StartupLocation.MachineRunOnce) && (!Item.RequiresAdmin || ElevationHelper.IsElevated);
    public string ExeName => PathUtil.LeafName(Item.ExePath ?? Item.Command);

    partial void OnEnabledChanged(bool value) => _owner.Toggle(this, value);
}

public sealed partial class StartupViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _suppress;

    public StartupViewModel(AppServices services)
    {
        _services = services;
        Refresh();
    }

    public ObservableCollection<StartupItemViewModel> Items { get; } = new();
    [ObservableProperty] private StartupItemViewModel? _selectedItem;
    [ObservableProperty] private string _statusText = "";
    public bool IsElevated => ElevationHelper.IsElevated;
    public string Summary => $"{Items.Count} startup items · {Items.Count(i => i.Enabled)} enabled";
    public event Action? RequestClose;

    [RelayCommand]
    public void Refresh()
    {
        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var i in _services.Startup.GetItems()) Items.Add(new StartupItemViewModel(i, this));
        }
        finally { _suppress = false; }
        OnPropertyChanged(nameof(Summary));
    }

    internal void Toggle(StartupItemViewModel vm, bool enabled)
    {
        if (_suppress) return;
        var (ok, err) = _services.Startup.SetEnabled(vm.Item, enabled);
        if (!ok)
        {
            _suppress = true;
            vm.Enabled = !enabled;
            _suppress = false;
            vm.Status = err ?? "Failed";
            Dialogs.Error(err ?? "Could not change the startup item.");
        }
        else vm.Status = enabled ? "Enabled" : "Disabled";
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Delete(StartupItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        if (!Dialogs.Confirm($"Remove \"{t.Name}\" from startup?\n\n{t.Command}\n\nThe program itself is not uninstalled; it just will not start automatically any more.", destructive: true)) return;
        var (ok, err) = _services.Startup.Delete(t.Item);
        if (!ok) Dialogs.Error(err ?? "Failed.");
        Refresh();
    }

    [RelayCommand]
    private void OpenLocation(StartupItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        var exe = t.Item.ExePath;
        if (!string.IsNullOrEmpty(exe) && (File.Exists(exe) || Directory.Exists(exe))) Dialogs.OpenFolder(exe);
        else if (t.Item.Source.StartsWith("HK", StringComparison.OrdinalIgnoreCase))
        {
            var reg = t.Item.Source.Split(" → ")[0].Replace("HKCU", "HKEY_CURRENT_USER").Replace("HKLM", "HKEY_LOCAL_MACHINE");
            Dialogs.OpenRegistryKey(reg);
        }
    }

    [RelayCommand]
    private void SearchWeb(StartupItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t != null) Dialogs.OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(t.ExeName + " startup what is it"));
    }

    [RelayCommand] private void Close() => RequestClose?.Invoke();
}
