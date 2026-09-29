using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed class ToolCardViewModel
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Glyph { get; init; }
    public required string Key { get; init; }
    public bool RequiresAdmin { get; init; }
}

public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public ToolsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Tools = new ObservableCollection<ToolCardViewModel>
        {
            new() { Key = "force", Glyph = "", Title = "Force Uninstall", Description = "Remove a program whose uninstaller is broken or missing – pick it from the list or point at its folder." },
            new() { Key = "widget", Glyph = "", Title = "Easy Uninstall widget", Description = "A small floating target: drag it onto any program window (or drop a shortcut on it) to uninstall that program." },
            new() { Key = "residual", Glyph = "", Title = "Residual Cleaner", Description = "Find files, folders and registry keys left behind by programs that were uninstalled earlier – by any uninstaller." },
            new() { Key = "cleanup", Glyph = "", Title = "System Cleanup", Description = "Orphaned installer packages, removed Store apps' data, update caches, temp files and crash dumps.", RequiresAdmin = true },
            new() { Key = "startup", Glyph = "", Title = "Startup Apps", Description = "See everything that launches at sign-in; switch entries off or remove them." },
            new() { Key = "shred", Glyph = "", Title = "File Shredder", Description = "Permanently destroy files and folders by overwriting them so they cannot be recovered." },
            new() { Key = "updates", Glyph = "", Title = "Windows Updates", Description = "List installed Windows updates (KB…) and uninstall a problematic one.", RequiresAdmin = true },
            new() { Key = "restore", Glyph = "", Title = "Create Restore Point", Description = "Create a System Restore point right now, before you make risky changes.", RequiresAdmin = true },
            new() { Key = "appwiz", Glyph = "", Title = "Programs and Features", Description = "Open the classic Windows Control Panel uninstall list." },
            new() { Key = "storage", Glyph = "", Title = "Storage Settings", Description = "Open Windows Storage settings to see what is filling the disk." },
            new() { Key = "logs", Glyph = "", Title = "Evict Data Folder", Description = "Open the folder with settings, history, install logs and the diagnostic log." },
        };
    }

    public ObservableCollection<ToolCardViewModel> Tools { get; }
    [ObservableProperty] private string _statusText = "";

    [RelayCommand]
    private async Task RunToolAsync(ToolCardViewModel? tool)
    {
        if (tool is null) return;
        switch (tool.Key)
        {
            case "force":
            {
                var programs = _main.GetPage<ProgramsViewModel>(PageKey.Programs).Items.Select(i => i.Program).ToList();
                if (programs.Count == 0)
                    programs = await Task.Run(() => _services.Programs.Enumerate(new ProgramsQueryOptions()));
                var vm = new ForceUninstallViewModel(_services, null, programs);
                new ForceUninstallWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
                if (vm.AnythingChanged) _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
                break;
            }
            case "widget":
                _main.ShowWidgetCommand.Execute(null);
                break;
            case "residual":
                OpenResidualCleaner();
                break;
            case "cleanup":
                OpenSystemCleanup();
                break;
            case "startup":
                new StartupWindow { DataContext = new StartupViewModel(_services), Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
                break;
            case "shred":
                new FileShredderWindow { DataContext = new FileShredderViewModel(_services), Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
                break;
            case "updates":
                new WindowsUpdatesWindow { DataContext = new WindowsUpdatesViewModel(_services), Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
                break;
            case "restore":
            {
                if (!ElevationHelper.IsElevated) { Dialogs.Info("Creating a restore point requires administrator rights. Restart Evict as administrator."); return; }
                StatusText = "Creating restore point…";
                var r = await _services.RestorePoints.CreateAsync("Evict: manual restore point", CancellationToken.None);
                StatusText = "";
                Dialogs.Info(r.Message);
                break;
            }
            case "appwiz": Start("control.exe", "appwiz.cpl"); break;
            case "storage": Dialogs.OpenUrl("ms-settings:storagesense"); break;
            case "logs": Dialogs.OpenFolder(AppPaths.DataRoot); break;
        }
    }

    public void OpenSystemCleanup()
    {
        var vm = new SystemCleanupViewModel(_services);
        new SystemCleanupWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        if (vm.AnythingChanged) _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
    }

    public void OpenResidualCleaner()
    {
        var programs = _main.GetPage<ProgramsViewModel>(PageKey.Programs).Items.Select(i => i.Program).ToList();
        var vm = new ResidualViewModel(_services, programs);
        new ResidualWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow }.ShowDialog();
        if (vm.AnythingChanged)
        {
            _ = _main.GetPage<ProgramsViewModel>(PageKey.Programs).RefreshAsync();
            _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
        }
    }

    private static void Start(string file, string args)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error(ex.Message); }
    }
}

// ───────────────────────────── File Shredder ─────────────────────────────

public sealed partial class FileShredderViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public FileShredderViewModel(AppServices services)
    {
        _services = services;
        _method = Enum.TryParse<ShredMethod>(services.Settings.Current.ShredMethod, out var m) ? m : ShredMethod.Dod3Pass;
    }

    public ObservableCollection<string> Paths { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public IReadOnlyList<KeyValuePair<ShredMethod, string>> Methods { get; } = new[]
    {
        new KeyValuePair<ShredMethod, string>(ShredMethod.Quick, "Quick – 1 pass"),
        new KeyValuePair<ShredMethod, string>(ShredMethod.Dod3Pass, "Secure – 3 passes (DoD)"),
        new KeyValuePair<ShredMethod, string>(ShredMethod.Dod7Pass, "Paranoid – 7 passes"),
    };

    [ObservableProperty] private ShredMethod _method;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _selectedPath;
    [ObservableProperty] private int _filesShredded;
    [ObservableProperty] private long _bytesOverwritten;

    public bool HasPaths => Paths.Count > 0;
    public string MethodDescription => Method switch
    {
        ShredMethod.Quick => "1 pass of zeros – fast, defeats undelete tools.",
        ShredMethod.Dod3Pass => "3 passes (zeros, ones, random) – DoD 5220.22-M style. Recommended.",
        _ => "7 passes of alternating patterns and random data – slow, for the paranoid.",
    };
    public string BytesText => SizeFormatter.Format(BytesOverwritten);
    public event Action? RequestClose;

    partial void OnMethodChanged(ShredMethod value)
    {
        OnPropertyChanged(nameof(MethodDescription));
        _services.Settings.Current.ShredMethod = value.ToString();
        _services.Settings.Save();
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p) || Paths.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            if (PathUtil.IsProtectedRoot(p, checkProtectedNames: false) && Directory.Exists(p))
            {
                Dialogs.Info($"\"{p}\" is a protected system folder and cannot be shredded.");
                continue;
            }
            Paths.Add(p);
        }
        OnPropertyChanged(nameof(HasPaths));
        ShredCommand.NotifyCanExecuteChanged();
        IsDone = false;
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files to shred" };
        if (dlg.ShowDialog() == true) AddPaths(dlg.FileNames);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder to shred (everything inside will be destroyed)" };
        if (dlg.ShowDialog() == true) AddPaths(new[] { dlg.FolderName });
    }

    [RelayCommand]
    private void RemovePath(string? path)
    {
        var p = path ?? SelectedPath;
        if (p != null) Paths.Remove(p);
        OnPropertyChanged(nameof(HasPaths));
        ShredCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearPaths()
    {
        Paths.Clear();
        OnPropertyChanged(nameof(HasPaths));
        ShredCommand.NotifyCanExecuteChanged();
    }

    private bool CanShred() => Paths.Count > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanShred))]
    private async Task ShredAsync()
    {
        int folders = Paths.Count(Directory.Exists);
        int files = Paths.Count - folders;
        if (!Dialogs.Confirm($"Permanently destroy {files} file(s) and {folders} folder(s)?\n\nThis CANNOT be undone – the data will not be in the Recycle Bin and cannot be recovered.", destructive: true))
            return;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsDone = false;
        Errors.Clear();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var result = await _services.Shredder.ShredAsync(Paths.ToList(), Method, progress, _cts.Token);
            FilesShredded = result.FilesShredded;
            BytesOverwritten = result.BytesOverwritten;
            OnPropertyChanged(nameof(BytesText));
            foreach (var (p, e) in result.Errors) Errors.Add($"{p}: {e}");
            StatusText = $"Shredded {result.FilesShredded} file(s), removed {result.FoldersRemoved} folder(s)" + (result.Errors.Count > 0 ? $", {result.Errors.Count} error(s)." : ".");
            Paths.Clear();
            OnPropertyChanged(nameof(HasPaths));
            IsDone = true;
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex) { StatusText = "Failed: " + ex.Message; }
        finally { IsBusy = false; ShredCommand.NotifyCanExecuteChanged(); }
    }

    [RelayCommand] private void Cancel() { if (IsBusy) _cts?.Cancel(); else RequestClose?.Invoke(); }
    [RelayCommand] private void Close() => RequestClose?.Invoke();
}

// ───────────────────────────── Windows Updates ─────────────────────────────

public sealed partial class UpdateItemViewModel : ObservableObject
{
    public UpdateItemViewModel(WindowsUpdateInfo info) => Info = info;
    public WindowsUpdateInfo Info { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";
    public string Id => Info.HotFixId;
    public string Description => Info.Description ?? "";
    public string InstalledOn => Info.InstalledOn is { } d ? d.ToString("dd MMM yyyy") : "—";
    public string InstalledBy => Info.InstalledBy ?? "";
    public string Url => "https://support.microsoft.com/help/" + Info.KbNumber;
}

public sealed partial class WindowsUpdatesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public WindowsUpdatesViewModel(AppServices services)
    {
        _services = services;
        _ = RefreshAsync();
    }

    public ObservableCollection<UpdateItemViewModel> Items { get; } = new();
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _rebootRequired;
    [ObservableProperty] private UpdateItemViewModel? _selectedItem;
    public bool IsElevated => ElevationHelper.IsElevated;
    public event Action? RequestClose;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = "Reading installed updates…";
        try
        {
            var (list, err) = await _services.Updates.GetUpdatesAsync(CancellationToken.None);
            Items.Clear();
            foreach (var u in list) Items.Add(new UpdateItemViewModel(u));
            Error = list.Count == 0 ? err : null;
            StatusText = $"{Items.Count} update(s) installed.";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0 && SelectedItem != null) targets.Add(SelectedItem);
        if (targets.Count == 0) return;
        if (!IsElevated) { Dialogs.Info("Uninstalling Windows updates requires administrator rights. Restart Evict as administrator."); return; }
        if (!Dialogs.Confirm($"Uninstall {targets.Count} Windows update(s)?\n\n{string.Join("\n", targets.Select(t => "  • " + t.Id + "  " + t.Description))}\n\nWindows may reinstall them automatically unless you pause updates.", destructive: true))
            return;
        IsBusy = true;
        try
        {
            foreach (var t in targets)
            {
                StatusText = $"Uninstalling {t.Id}… (wusa.exe)";
                t.Status = "Uninstalling…";
                var (ok, msg, reboot) = await _services.Updates.UninstallAsync(t.Info, CancellationToken.None);
                t.Status = msg;
                if (reboot) RebootRequired = true;
                _services.History.Add(new UninstallHistoryEntry { ProgramName = t.Id + " " + t.Description, Method = UninstallMethod.Standard, Succeeded = ok, Notes = msg, Publisher = "Microsoft" });
            }
            StatusText = "Done.";
        }
        finally { IsBusy = false; }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenKbPage(UpdateItemViewModel? item) => Dialogs.OpenUrl((item ?? SelectedItem)?.Url);

    [RelayCommand] private void Close() => RequestClose?.Invoke();
}
