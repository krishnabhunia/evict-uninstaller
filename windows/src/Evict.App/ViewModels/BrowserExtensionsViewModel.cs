using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.App.ViewModels;

public sealed partial class ExtensionItemViewModel : ObservableObject
{
    public ExtensionItemViewModel(BrowserExtensionInfo info) => Info = info;
    public BrowserExtensionInfo Info { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    public string Name => Info.Name;
    public string Version => Info.Version ?? "—";
    public string Browser => Info.BrowserDisplayName;
    public string Profile => Info.ProfileName;
    public string Group => $"{Info.BrowserDisplayName} — {Info.ProfileName}";
    public string StateText => Info.Enabled ? "Enabled" : "Disabled";
    public string Source => Info.Source;
    public string SizeText => SizeFormatter.Format(Info.SizeBytes);
    public string InstalledText => Info.InstallTime is { } d ? d.ToString("dd MMM yyyy") : "—";
    public string Description => Info.Description ?? "";
    public string PermissionsText => Info.Permissions.Count == 0 ? "No special permissions" : string.Join(", ", Info.Permissions);
    public bool IsRisky => IsRiskyInfo(Info);
    public static bool IsRiskyInfo(BrowserExtensionInfo info) =>
        !info.IsComponent && (info.Permissions.Any(p => p is "<all_urls>" or "webRequest" or "webRequestBlocking" or "history" or "cookies" or "clipboardRead" or "debugger" or "nativeMessaging" or "proxy")
                              || info.Permissions.Any(p => p.Contains("://*/*")));
    public bool CanRemove => !Info.IsComponent && !Info.InstalledByPolicy;
    public string BrowserGlyph => Info.Browser == BrowserKind.Firefox ? "" : "";
    public string Tooltip => $"{Name} {Version}\nID: {Info.ExtensionId}\n{Description}\n\nPermissions: {PermissionsText}";

    public bool Matches(string s) => string.IsNullOrWhiteSpace(s) || Name.Contains(s, StringComparison.OrdinalIgnoreCase)
        || Info.ExtensionId.Contains(s, StringComparison.OrdinalIgnoreCase) || Browser.Contains(s, StringComparison.OrdinalIgnoreCase) || Description.Contains(s, StringComparison.OrdinalIgnoreCase);
}

public sealed partial class BrowserExtensionsViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private bool _loaded;

    public BrowserExtensionsViewModel(AppServices services)
    {
        _services = services;
        Items = new ObservableCollection<ExtensionItemViewModel>();
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is ExtensionItemViewModel e && e.Matches(SearchText) && (ShowBuiltIn || !e.Info.IsComponent);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ExtensionItemViewModel.Group)));
        View.SortDescriptions.Add(new SortDescription(nameof(ExtensionItemViewModel.Group), ListSortDirection.Ascending));
        View.SortDescriptions.Add(new SortDescription(nameof(ExtensionItemViewModel.Name), ListSortDirection.Ascending));
    }

    public ObservableCollection<ExtensionItemViewModel> Items { get; }
    public ICollectionView View { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showBuiltIn;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private ExtensionItemViewModel? _selectedItem;

    public int Count => Items.Count;
    public int RiskyCount => Items.Count(i => i.IsRisky);
    public string Summary => Items.Count == 0 ? "No extensions found." : $"{Items.Count} extensions across {Items.Select(i => i.Group).Distinct().Count()} browser profile(s) · {RiskyCount} with broad permissions";

    public void OnActivated() { if (!_loaded) _ = RefreshAsync(); }

    partial void OnSearchTextChanged(string value) => View.Refresh();
    partial void OnShowBuiltInChanged(bool value) => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Reading browser profiles…";
        try
        {
            var list = await _services.Browser.GetExtensionsAsync(ShowBuiltIn, CancellationToken.None);
            foreach (var i in Items) i.PropertyChanged -= ItemChanged;
            Items.Clear();
            foreach (var e in list)
            {
                var vm = new ExtensionItemViewModel(e);
                vm.PropertyChanged += ItemChanged;
                Items.Add(vm);
            }
            _loaded = true;
            StatusText = "";
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(RiskyCount));
            OnPropertyChanged(nameof(Summary));
            UpdateSelection();
        }
        catch (Exception ex) { StatusText = "Failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void ItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExtensionItemViewModel.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        RemoveSelectedCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemove() => SelectedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;
        var running = targets.Select(t => t.Info.Browser).Distinct().Where(BrowserExtensionService.IsBrowserRunning).ToList();
        if (running.Count > 0)
        {
            Dialogs.Info("Please close " + string.Join(" and ", running.Select(BrowserName)) +
                         " completely (including background processes in the system tray), then try again.\n\nExtensions can only be removed while the browser is closed.");
            return;
        }
        if (!Dialogs.Confirm($"Remove {targets.Count} extension(s)?\n\n{string.Join("\n", targets.Take(10).Select(t => "  • " + t.Name + " (" + t.Browser + ")"))}\n\nA backup of each browser's preferences file is kept next to it (*.evict-backup).", destructive: true))
            return;

        IsBusy = true;
        try
        {
            foreach (var t in targets)
            {
                StatusText = $"Removing {t.Name}…";
                var (ok, msg) = await _services.Browser.RemoveAsync(t.Info, CancellationToken.None);
                t.Status = msg;
                _services.History.Add(new UninstallHistoryEntry
                {
                    ProgramName = $"{t.Name} ({t.Browser} extension)", Publisher = t.Info.HomepageUrl, Version = t.Info.Version, Method = UninstallMethod.Standard,
                    Succeeded = ok, Notes = msg, InstallLocation = t.Info.ExtensionPath, BytesReclaimed = ok ? t.Info.SizeBytes : 0,
                });
            }
        }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenExtensionsPage(ExtensionItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        try
        {
            if (t.Info.Browser == BrowserKind.Firefox)
            {
                var ff = FindBrowserExe("firefox.exe");
                if (ff != null) Process.Start(new ProcessStartInfo(ff, "about:addons") { UseShellExecute = true });
                return;
            }
            var exe = t.Info.Browser switch
            {
                BrowserKind.Chrome => FindBrowserExe("chrome.exe"),
                BrowserKind.Edge => FindBrowserExe("msedge.exe"),
                BrowserKind.Brave => FindBrowserExe("brave.exe"),
                BrowserKind.Vivaldi => FindBrowserExe("vivaldi.exe"),
                BrowserKind.Opera => FindBrowserExe("opera.exe") ?? FindBrowserExe("launcher.exe"),
                _ => FindBrowserExe("chrome.exe"),
            };
            if (exe is null) { Dialogs.Info("Could not locate the browser executable."); return; }
            var scheme = t.Info.Browser == BrowserKind.Edge ? "edge" : t.Info.Browser == BrowserKind.Brave ? "brave" : t.Info.Browser == BrowserKind.Vivaldi ? "vivaldi" : t.Info.Browser == BrowserKind.Opera ? "opera" : "chrome";
            var profileDir = PathUtil.LeafName(t.Info.ProfilePath);
            Process.Start(new ProcessStartInfo(exe, $"--profile-directory=\"{profileDir}\" {scheme}://extensions/?id={t.Info.ExtensionId}") { UseShellExecute = true });
        }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }

    [RelayCommand]
    private void OpenFolder(ExtensionItemViewModel? item) => Dialogs.OpenFolder((item ?? SelectedItem)?.Info.ExtensionPath);

    [RelayCommand]
    private void OpenHomepage(ExtensionItemViewModel? item)
    {
        var t = item ?? SelectedItem;
        if (t is null) return;
        var url = t.Info.HomepageUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = t.Info.Browser == BrowserKind.Firefox
                ? "https://addons.mozilla.org/firefox/search/?q=" + Uri.EscapeDataString(t.Name)
                : "https://chromewebstore.google.com/detail/" + t.Info.ExtensionId;
        Dialogs.OpenUrl(url);
    }

    private static string BrowserName(BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Brave => "Brave",
        BrowserKind.Vivaldi => "Vivaldi",
        BrowserKind.Opera => "Opera",
        BrowserKind.Firefox => "Mozilla Firefox",
        _ => kind.ToString(),
    };

    private static string? FindBrowserExe(string exeName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                    var p = k?.GetValue("") as string;
                    if (!string.IsNullOrEmpty(p) && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { /* ignore */ }
            }
        }
        return null;
    }
}
