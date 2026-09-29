using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

public sealed partial class ToggleItemViewModel : ObservableObject
{
    private readonly ToggleListViewModel _owner;

    public ToggleItemViewModel(ToggleItem item, ToggleListViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isOn = item.IsOn;
    }

    public ToggleItem Item { get; }
    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private string _status = "";

    public string Name => Item.Name;
    public string Group => Item.Group;
    public string Detail => Item.Detail ?? "";
    public bool RecommendOff => Item.RecommendOff;
    public bool CanToggle => !Item.Locked;
    public string LockedReason => Item.LockedReason ?? "";

    partial void OnIsOnChanged(bool value) => _owner.Toggle(this, value);

    /// <summary>Sets the switch without writing anything (used to undo a failed change).</summary>
    internal void Revert(bool value)
    {
#pragma warning disable MVVMTK0034 // deliberately bypass OnIsOnChanged
        _isOn = value;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>Shared window for Software Health switch lists: notifications, app permissions, background services…</summary>
public sealed partial class ToggleListViewModel : ObservableObject
{
    private readonly IToggleProvider _provider;
    private bool _loading;

    public ToggleListViewModel(IToggleProvider provider)
    {
        _provider = provider;
        View = CollectionViewSource.GetDefaultView(Items);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ToggleItemViewModel.Group)));
        Refresh();
    }

    public string Title => _provider.Title;
    public string Subtitle => _provider.Subtitle;
    public string OnLabel => _provider.OnLabel;
    public ObservableCollection<ToggleItemViewModel> Items { get; } = new();
    public ICollectionView View { get; }
    [ObservableProperty] private string _statusText = "";
    public bool AnythingChanged { get; private set; }
    public int RecommendedOnCount => Items.Count(i => i.RecommendOff && i.IsOn && i.CanToggle);
    public bool HasRecommendedOn => RecommendedOnCount > 0;
    public string Summary => $"{Items.Count(i => i.IsOn)} of {Items.Count} {OnLabel.ToLowerInvariant()} · {RecommendedOnCount} recommended to switch off";
    public event Action? RequestClose;

    [RelayCommand]
    public void Refresh()
    {
        _loading = true;
        try
        {
            Items.Clear();
            foreach (var i in _provider.Load().OrderBy(i => i.Group).ThenByDescending(i => i.RecommendOff && i.IsOn).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
                Items.Add(new ToggleItemViewModel(i, this));
        }
        catch (Exception ex) { StatusText = "Could not read the list: " + ex.Message; }
        finally { _loading = false; }
        RaiseCounts();
    }

    internal void Toggle(ToggleItemViewModel vm, bool on)
    {
        if (_loading) return;
        var err = _provider.Set(vm.Item, on);
        if (err != null)
        {
            vm.Revert(!on);
            vm.Status = "Failed";
            StatusText = $"{vm.Name}: {err}";
        }
        else
        {
            vm.Status = on ? "Switched on" : "Switched off";
            AnythingChanged = true;
        }
        RaiseCounts();
    }

    [RelayCommand]
    private void SwitchOffRecommended()
    {
        var targets = Items.Where(i => i.RecommendOff && i.IsOn && i.CanToggle).ToList();
        if (targets.Count == 0) return;
        if (!Dialogs.Confirm($"Switch off {targets.Count} recommended item(s)?\n\n" + string.Join("\n", targets.Take(12).Select(t => "•  " + t.Name)) + (targets.Count > 12 ? $"\n… and {targets.Count - 12} more" : "") + "\n\nYou can switch any of them on again here.")) return;
        foreach (var t in targets) t.IsOn = false;
        StatusText = $"Switched off {targets.Count(t => !t.IsOn)} item(s).";
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(RecommendedOnCount));
        OnPropertyChanged(nameof(HasRecommendedOn));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand] private void Close() => RequestClose?.Invoke();
}
