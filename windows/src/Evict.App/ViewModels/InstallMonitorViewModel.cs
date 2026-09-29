using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public sealed partial class InstallLogItemViewModel : ObservableObject
{
    public InstallLogItemViewModel(InstallLog log) => Log = log;
    public InstallLog Log { get; }
    public string Title => Log.Title;
    public string InstallerName => PathUtil.LeafName(Log.InstallerPath);
    public string DateText => Log.Started.ToString("dd MMM yyyy HH:mm");
    public string SizeText => SizeFormatter.Format(Log.TotalBytes);
    public int FileCount => Log.CreatedDirectories.Count + Log.CreatedFiles.Count;
    public int RegistryCount => Log.CreatedRegistryKeys.Count;
    public string Summary => $"{Log.CreatedDirectories.Count} folders · {Log.CreatedFiles.Count} files · {Log.CreatedRegistryKeys.Count} registry keys" + (Log.NewUninstallEntries.Count > 0 ? " · registered in Programs & Features" : "");
    public bool Uninstalled => Log.Uninstalled;
    public string StatusText => Log.Uninstalled ? "Uninstalled" : Log.InstallerExitCode is { } c && c != 0 ? $"Installer exit code {c}" : "Installed";

    public IEnumerable<string> FileEntries => Log.CreatedDirectories.Select(d => "📁 " + d).Concat(Log.CreatedFiles.Select(f => "📄 " + f));
    public IEnumerable<string> RegistryEntries => Log.CreatedRegistryKeys.Select(InstallMonitorService.DisplaySnapshotKey);
    public IEnumerable<string> ModifiedEntries => Log.ModifiedFiles;
}

public sealed partial class InstallMonitorViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;

    public InstallMonitorViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
    }

    public ObservableCollection<InstallLogItemViewModel> Logs { get; } = new();

    /// <summary>Tray-side automatic recording state (shown as a banner on this page).</summary>
    public BackgroundCoordinator Background => _main.Background;
    [RelayCommand] private void CancelRecording() => _main.Background.CancelRecording();

    [ObservableProperty] private bool _isMonitoring;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private InstallLogItemViewModel? _selectedLog;
    [ObservableProperty] private InstallLogItemViewModel? _lastResult;

    public bool HasLogs => Logs.Count > 0;

    public void OnActivated() => Reload();

    public void Reload()
    {
        var selectedId = SelectedLog?.Log.Id;
        Logs.Clear();
        foreach (var l in _services.Monitor.LoadLogs()) Logs.Add(new InstallLogItemViewModel(l));
        SelectedLog = Logs.FirstOrDefault(l => l.Log.Id == selectedId) ?? Logs.FirstOrDefault();
        OnPropertyChanged(nameof(HasLogs));
    }

    [RelayCommand]
    private async Task MonitorNewInstallAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the installer to monitor",
            Filter = "Installers (*.exe;*.msi;*.msix;*.bat;*.cmd)|*.exe;*.msi;*.msix;*.bat;*.cmd|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        await MonitorAsync(dlg.FileName);
    }

    public async Task MonitorAsync(string installerPath)
    {
        if (IsMonitoring) return;
        IsMonitoring = true;
        ProgressIndeterminate = true;
        LastResult = null;
        var progress = new Progress<ProgressReport>(r =>
        {
            StatusText = r.Message;
            if (r.Percent is { } p) { ProgressIndeterminate = false; Progress = p; }
        });
        try
        {
            string path = installerPath;
            string? args = null;
            if (path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                args = $"/i \"{path}\"";
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            }
            var log = await _services.Monitor.MonitorInstallAsync(path, args, progress, CancellationToken.None);
            if (path.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)) { log.Title = log.Title == "msiexec" ? PathUtil.LeafName(installerPath) : log.Title; _services.Monitor.SaveLog(log); }
            Reload();
            LastResult = Logs.FirstOrDefault(l => l.Log.Id == log.Id);
            SelectedLog = LastResult;
            StatusText = $"Recorded {log.CreatedDirectories.Count} folders, {log.CreatedFiles.Count} files and {log.CreatedRegistryKeys.Count} registry keys.";
        }
        catch (Exception ex)
        {
            StatusText = "Monitoring failed: " + ex.Message;
            Dialogs.Error("Install monitoring failed:\n" + ex.Message);
        }
        finally { IsMonitoring = false; }
    }

    [RelayCommand]
    private async Task UninstallWithLogAsync(InstallLogItemViewModel? item)
    {
        var target = item ?? SelectedLog;
        if (target is null) return;
        var log = target.Log;

        // 1. If the install registered itself, run the regular uninstaller first (through the normal wizard).
        InstalledProgram? program = null;
        if (log.NewUninstallEntries.Count > 0)
        {
            var all = await Task.Run(() => _services.Programs.Enumerate(new ProgramsQueryOptions { IncludeSystemComponents = true, IncludeUpdates = true }));
            foreach (var entry in log.NewUninstallEntries)
            {
                var (_, _, sub) = InstallMonitorService.ParseSnapshotKey(entry);
                var keyName = PathUtil.LeafName(sub);
                program = all.FirstOrDefault(p => p.KeyName.Equals(keyName, StringComparison.OrdinalIgnoreCase));
                if (program != null) break;
            }
        }

        if (program != null)
        {
            var wizard = new UninstallWizardViewModel(_services, new[] { program });
            var window = new UninstallWizardWindow { DataContext = wizard, Owner = System.Windows.Application.Current.MainWindow };
            window.ShowDialog();
            if (!wizard.AnythingChanged) return;
        }

        // 2. Remove everything the monitor recorded that still exists.
        var leftovers = await Task.Run(() => InstallMonitorService.ToLeftovers(log));
        if (leftovers.Count == 0)
        {
            log.Uninstalled = true;
            _services.Monitor.SaveLog(log);
            Reload();
            Dialogs.Info("Nothing recorded by the monitor remains on disk – the program is fully removed.");
            return;
        }
        var force = new ForceUninstallViewModel(_services, log.Title, log.CreatedDirectories.FirstOrDefault());
        force.PreloadLeftovers(leftovers);
        var fw = new ForceUninstallWindow { DataContext = force, Owner = System.Windows.Application.Current.MainWindow };
        fw.ShowDialog();
        if (force.AnythingChanged)
        {
            log.Uninstalled = true;
            _services.Monitor.SaveLog(log);
            Reload();
            _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
        }
    }

    [RelayCommand]
    private void DeleteLog(InstallLogItemViewModel? item)
    {
        var target = item ?? SelectedLog;
        if (target is null) return;
        if (!Dialogs.Confirm($"Delete the install log for \"{target.Title}\"? The installed program itself is not touched.")) return;
        _services.Monitor.DeleteLog(target.Log);
        Reload();
    }

    [RelayCommand]
    private void OpenInstallerFolder(InstallLogItemViewModel? item) => Dialogs.OpenFolder((item ?? SelectedLog)?.Log.InstallerPath);

    [RelayCommand]
    private void ExportLog(InstallLogItemViewModel? item)
    {
        var target = item ?? SelectedLog;
        if (target is null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{target.Title}-install-log.txt", Filter = "Text file|*.txt" };
        if (dlg.ShowDialog() != true) return;
        var lines = new List<string> { $"Install log: {target.Title}", $"Installer: {target.Log.InstallerPath}", $"Started: {target.Log.Started}", "", "== Folders ==" };
        lines.AddRange(target.Log.CreatedDirectories);
        lines.Add(""); lines.Add("== Files =="); lines.AddRange(target.Log.CreatedFiles);
        lines.Add(""); lines.Add("== Modified files =="); lines.AddRange(target.Log.ModifiedFiles);
        lines.Add(""); lines.Add("== Registry keys =="); lines.AddRange(target.RegistryEntries);
        File.WriteAllLines(dlg.FileName, lines);
    }
}
