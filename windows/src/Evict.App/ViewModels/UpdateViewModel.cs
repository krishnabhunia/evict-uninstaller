using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

/// <summary>
/// "A new version is available" dialog: release notes, download with progress, and the install / self-replace step.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly AppServices _services;

    public UpdateViewModel(AppServices services, ReleaseInfo release)
    {
        _services = services;
        Release = release;
        InstalledMode = UpdateService.IsInstalledMode();
        Asset = UpdateChecker.PickAsset(release, InstalledMode);
        if (Asset is null && InstalledMode) { Asset = UpdateChecker.PickAsset(release, false); FallbackToPortable = Asset != null; }
    }

    public ReleaseInfo Release { get; }
    public ReleaseAsset? Asset { get; private set; }
    public bool InstalledMode { get; }
    public bool FallbackToPortable { get; private set; }
    public bool CanInstall => Asset != null && !(InstalledMode && FallbackToPortable);

    public string Title => $"Evict {Release.Version.ToString(3)} is available";
    public string Subtitle => $"You are running {_services.Updater.CurrentVersion.ToString(3)}." +
                              (Release.PublishedAt is { } p ? $"  Released {p.LocalDateTime:d}." : "") +
                              (Release.Prerelease ? "  This is a pre-release." : "");
    public string Notes => string.IsNullOrWhiteSpace(Release.Body) ? "No release notes were published for this version." : Release.Body!.Trim();
    public string ModeText => Asset is null
        ? "This release has no downloadable file for your edition. Use the link below to download it from GitHub."
        : InstalledMode && !FallbackToPortable
            ? $"Evict was installed with Setup. The new installer{SizeText} will be downloaded and run silently; Evict restarts when it is done."
            : InstalledMode
                ? "Evict was installed with Setup but this release only ships the portable Evict.exe. Please download it from GitHub and run the installer manually."
                : $"Portable edition: the new Evict.exe{SizeText} is downloaded to your data folder, verified, swapped in place and Evict restarts.";
    private string SizeText => Asset is { Size: > 0 } ? $" ({SizeFormatter.Format(Asset.Size)})" : "";

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;          // 0..100
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _error;

    public event Action? RequestClose;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (Asset is null || !CanInstall) return;
        Error = null;
        IsBusy = true;
        IsIndeterminate = true;
        StatusText = "Connecting…";
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    IsIndeterminate = false;
                    Progress = Math.Min(100, p.Done * 100.0 / p.Total);
                    StatusText = $"Downloading… {SizeFormatter.Format(p.Done)} of {SizeFormatter.Format(p.Total)}";
                }
                else StatusText = $"Downloading… {SizeFormatter.Format(p.Done)}";
            });
            var path = await _services.Updater.DownloadAsync(Release, Asset, progress, ct);

            IsIndeterminate = true;
            StatusText = InstalledMode ? "Starting the installer…" : "Replacing Evict.exe…";
            await Task.Delay(300, ct);

            var (ok, error) = _services.Updater.Apply(path, InstalledMode, beforeRestart: Program.ReleaseSingleInstance);
            if (ok)
            {
                _services.Settings.Current.SkippedUpdateVersion = null;
                _services.Settings.Save();
                _ = Application.Current.Dispatcher.BeginInvoke(App.Quit);
                return;
            }
            Error = error;
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        catch (Exception ex)
        {
            Log.Error("Update failed", ex);
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    [RelayCommand] private void OpenReleasePage() => Dialogs.OpenUrl(Release.HtmlUrl);

    [RelayCommand]
    private void SkipVersion()
    {
        _services.Settings.Current.SkippedUpdateVersion = Release.TagName;
        _services.Settings.Save();
        RequestClose?.Invoke();
    }

    [RelayCommand] private void Later() { if (IsBusy) InstallCancelCommand.Execute(null); else RequestClose?.Invoke(); }
}
