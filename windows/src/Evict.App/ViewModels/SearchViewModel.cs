using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

/// <summary>The sections of the Settings page (RD1): the left list and the Ctrl+K "Settings" results.</summary>
public static class SettingsSections
{
    public sealed record Section(string Name, string Glyph, string Keywords);

    public static readonly IReadOnlyList<Section> All =
    [
        new("General", "", "program list sizes recently installed large rarely used days hidden system components Explorer context menu right-click integration"),
        new("Uninstalling", "", "uninstall restore point leftovers recycle bin scan quiet silent force"),
        new("Updates", "", "update check github beta preview prerelease internet firewall automatic"),
        new("Background & tray", "", "notification area tray start with windows sign-in close minimize installer detection scheduled scan daily weekly"),
        new("Appearance", "", "theme dark light system text size zoom bigger smaller"),
        new("Advanced", "", "administrator rights elevation always run as admin"),
        new("About", "", "version licence license log folder diagnostics website"),
    ];
}

public sealed class SearchResultViewModel
{
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public required string Glyph { get; init; }
    public string? Keywords { get; init; }
    public required Action Run { get; init; }
}

/// <summary>Ctrl+K: one box that finds programs, pages, tools and settings (RD1).</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly List<SearchResultViewModel> _apps;
    private readonly List<SearchResultViewModel> _actions;
    private readonly List<SearchResultViewModel> _settings;

    public SearchViewModel(MainViewModel main)
    {
        var programs = main.IsPageLoaded(PageKey.Programs) ? main.GetPage<ProgramsViewModel>(PageKey.Programs) : null;
        _apps = (programs?.Items ?? []).Select(i => new SearchResultViewModel
        {
            Group = "Apps", Title = i.Name, Detail = i.Publisher == "—" ? i.SizeText : $"{i.Publisher} · {i.SizeText}", Glyph = "",
            Run = () => { main.Navigate(PageKey.Programs); programs!.SelectedTab = ProgramTab.All; programs.SearchText = i.Name; programs.SelectedItem = i; },
        }).ToList();

        _actions = [];
        foreach (var key in Enum.GetValues<PageKey>())
        {
            var info = MainViewModel.PageInfo(key);
            var title = info.Group == info.Tab ? info.Group : $"{info.Group} → {info.Tab}";
            _actions.Add(new SearchResultViewModel { Group = "Actions", Title = "Open " + title, Detail = info.Subtitle, Glyph = "", Keywords = info.Tab, Run = () => main.Navigate(key) });
        }
        var tools = main.GetPage<ToolsViewModel>(PageKey.Tools);
        foreach (var tool in tools.Tools)
            _actions.Add(new SearchResultViewModel
            {
                Group = "Actions", Title = tool.Title, Detail = tool.Description, Glyph = tool.Glyph, Keywords = tool.Description,
                Run = () => { main.Navigate(PageKey.Tools); _ = tools.RunToolCommand.ExecuteAsync(tool); },
            });
        if (programs != null)
            _actions.Add(new SearchResultViewModel { Group = "Actions", Title = "Uninstall several programs", Detail = "Tick them in Apps → Programs", Glyph = "", Keywords = "batch multiple remove", Run = () => main.Navigate(PageKey.Programs) });

        _settings = SettingsSections.All.Select(s => new SearchResultViewModel
        {
            Group = "Settings", Title = s.Name, Detail = "Settings", Glyph = s.Glyph, Keywords = s.Keywords,
            Run = () => { main.Navigate(PageKey.Settings); main.GetPage<SettingsViewModel>(PageKey.Settings).SelectedSection = s.Name; },
        }).ToList();

        View = CollectionViewSource.GetDefaultView(Results);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchResultViewModel.Group)));
        Refresh();
    }

    public ObservableCollection<SearchResultViewModel> Results { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private SearchResultViewModel? _selected;
    public bool IsEmpty => Results.Count == 0;
    public string HintText => _apps.Count == 0 ? "Open Apps once to search your programs here too." : $"{_apps.Count} programs, every page, tool and setting.";

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Results.Clear();
        bool empty = string.IsNullOrWhiteSpace(Query);
        foreach (var r in QuickSearch.Top(_apps, Query, r => r.Title, r => r.Detail, empty ? 0 : 6)) Results.Add(r);
        foreach (var r in QuickSearch.Top(_actions, Query, r => r.Title, r => r.Keywords, empty ? 6 : 8)) Results.Add(r);
        foreach (var r in QuickSearch.Top(_settings, Query, r => r.Title, r => r.Keywords, empty ? 3 : 5)) Results.Add(r);
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Moves the highlighted result (arrow keys while typing).</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var items = View.Cast<SearchResultViewModel>().ToList();
        int i = Selected is null ? -1 : items.IndexOf(Selected);
        Selected = items[Math.Clamp(i + delta, 0, items.Count - 1)];
    }
}
