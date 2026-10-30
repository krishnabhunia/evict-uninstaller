using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

/// <summary>One row of the version picker list.</summary>
public sealed class VersionOptionViewModel
{
    public VersionOptionViewModel(ReleaseInfo release, string? tag, bool onThisPc, bool canInstall, ReleaseAsset? asset)
    {
        Release = release;
        Tag = tag;
        IsOnThisPc = onThisPc;
        CanInstall = canInstall || onThisPc;
        Asset = asset;
    }

    public ReleaseInfo Release { get; }
    public ReleaseAsset? Asset { get; }
    public string Version => "v" + Release.DisplayVersion;
    public bool IsBeta => Release.IsPreview;
    public bool IsStable => !Release.IsPreview;
    public string? Tag { get; }
    public bool HasTag => !string.IsNullOrEmpty(Tag);
    public bool IsOnThisPc { get; }
    public bool CanInstall { get; }
    public bool IsDownload => !IsOnThisPc && CanInstall;
    public string DateText => Release.PublishedAt is { } p ? $"Released {p.LocalDateTime:d MMM yyyy}" : "Release date not published";
    public string SizeText => Asset is { Size: > 0 } a ? SizeFormatter.Format(a.Size) : "";
    public string Notes => string.IsNullOrWhiteSpace(Release.Body) ? "No release notes were published for this version." : Release.Body!.Trim();
}

/// <summary>
/// Version picker (design VP1): every newer and older Evict release, stable and beta, with nothing chosen for you.
/// Two tabs (Updates / Version history &amp; roll back), a filter, a detail pane, then a confirm step that does the change.
/// </summary>
public sealed partial class VersionPickerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly VersionArchive _archive = VersionArchive.Default;
    private IReadOnlyList<ReleaseInfo> _releases;

    public VersionPickerViewModel(AppServices services, MainViewModel main, IReadOnlyList<ReleaseInfo> releases, bool rollBack)
    {
        _services = services;
        _main = main;
        _releases = releases;
        InstalledMode = UpdateService.IsInstalledMode();
        _isHistoryTab = rollBack;
        _filter = !rollBack && !services.Settings.Current.IncludeBetaUpdates ? VersionFilter.Stable : VersionFilter.All;
        _keepCurrentCopy = services.Settings.Current.KeepVersionCopies;
        Rebuild();
    }

    private AppSettings S => _services.Settings.Current;
    public bool InstalledMode { get; }
    public string CurrentVersion => _services.Updater.CurrentVersionLabel;
    public string CurrentVersionText => $"Installed: v{CurrentVersion}" + (InstalledMode ? " · installed with Setup" : " · portable");

    // ───────────── tabs and filter ─────────────

    [ObservableProperty] private bool _isHistoryTab;
    public bool IsUpdatesTab { get => !IsHistoryTab; set { if (value) IsHistoryTab = false; } }
    partial void OnIsHistoryTabChanged(bool value)
    {
        OnPropertyChanged(nameof(IsUpdatesTab));
        IsConfirming = false;
        Rebuild();
    }

    [ObservableProperty] private VersionFilter _filter;
    partial void OnFilterChanged(VersionFilter value) { OnPropertyChanged(nameof(IsAll)); OnPropertyChanged(nameof(IsStableOnly)); OnPropertyChanged(nameof(IsBetaOnly)); Rebuild(); }
    public bool IsAll { get => Filter == VersionFilter.All; set { if (value) Filter = VersionFilter.All; } }
    public bool IsStableOnly { get => Filter == VersionFilter.Stable; set { if (value) Filter = VersionFilter.Stable; } }
    public bool IsBetaOnly { get => Filter == VersionFilter.Beta; set { if (value) Filter = VersionFilter.Beta; } }

    private IReadOnlyList<ReleaseInfo> TabReleases => IsHistoryTab
        ? VersionPicker.Older(_releases, CurrentVersion)
        : VersionPicker.Newer(_releases, CurrentVersion);

    public string AllText => $"All ({TabReleases.Count})";
    public string StableText => $"Stable ({TabReleases.Count(r => !r.IsPreview)})";
    public string BetaText => $"Beta ({TabReleases.Count(r => r.IsPreview)})";

    public ObservableCollection<VersionOptionViewModel> Items { get; } = new();
    public bool HasItems => Items.Count > 0;
    public string EmptyText => IsLoading ? "Getting the list of versions from GitHub…"
        : LoadError != null ? LoadError
        : IsHistoryTab ? "No older version is published for this filter."
        : "You have the newest version for this filter.";

    private void Rebuild()
    {
        var tab = TabReleases;
        Items.Clear();
        foreach (var r in VersionPicker.Filter(tab, Filter))
        {
            var asset = UpdateChecker.PickAsset(r, InstalledMode);
            bool onThisPc = asset != null && _archive.Has(r.DisplayVersion, asset.Name);
            Items.Add(new VersionOptionViewModel(r, VersionPicker.Tag(r, tab, S.PreviousVersion), onThisPc, VersionPicker.CanInstall(r, InstalledMode), asset));
        }
        Selected = null; // nothing is ever chosen for you
        foreach (var name in new[] { nameof(AllText), nameof(StableText), nameof(BetaText), nameof(HasItems), nameof(EmptyText), nameof(BannerText), nameof(Heading),
                     nameof(NoSelectionText), nameof(NoSelectionHint), nameof(ShowPin) })
            OnPropertyChanged(name);
    }

    public string Heading => IsHistoryTab ? "Version history & roll back" : "Updates";
    public string BannerText
    {
        get
        {
            if (IsHistoryTab) return "Every older release, stable and beta. Versions saved on this PC roll back without a download.";
            var offer = VersionPicker.OfferText(TabReleases);
            return offer.Length == 0 ? "No newer version is published." : offer + ". Nothing installs on its own — pick the exact version you want.";
        }
    }

    // ───────────── pin ─────────────

    public bool IsPinned => !string.IsNullOrWhiteSpace(S.PinnedVersion);
    public string PinnedText => IsPinned ? $"Pinned to v{S.PinnedVersion} — no update notices until you unpin it." : "";

    [RelayCommand]
    private void Unpin()
    {
        _main.Unpin();
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(PinnedText));
    }

    // ───────────── loading ─────────────

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _loadError;
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptyText));
    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(EmptyText));

    /// <summary>Reads the release list again (also used when the window opens without one).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading || IsBusy) return;
        IsLoading = true;
        LoadError = null;
        try
        {
            var (releases, error) = await _services.Updater.ListReleasesAsync(App.ShutdownToken);
            if (releases != null)
            {
                _releases = releases;
                _main.RememberReleases(releases);
            }
            else LoadError = error;
        }
        catch (OperationCanceledException) { /* closing */ }
        finally
        {
            IsLoading = false;
            Rebuild();
        }
    }

    public bool NeedsLoading => _releases.Count == 0;

    // ───────────── selection and detail ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(NoSelection), nameof(CanContinue), nameof(FromToText), nameof(SourceText),
        nameof(SelectedIsBeta), nameof(SelectedIsStable), nameof(SelectedCannotInstall), nameof(HasSettingsBackup), nameof(NoSettingsBackup),
        nameof(ContinueText), nameof(ShowPin))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private VersionOptionViewModel? _selected;

    public bool HasSelection => Selected != null;
    public bool NoSelection => Selected == null;
    public bool CanContinue => Selected is { CanInstall: true } && !IsBusy;
    public bool SelectedIsBeta => Selected?.IsBeta == true;
    public bool SelectedIsStable => Selected?.IsStable == true;
    public bool SelectedCannotInstall => Selected is { CanInstall: false };
    public string NoSelectionText => IsHistoryTab ? "Select a version to roll back to" : "Select a version to see what changes";
    public string NoSelectionHint => IsHistoryTab
        ? "Rolling back keeps a copy of the version you have now, so you can return to it."
        : "The newest build may be a beta. It is never selected for you — you decide.";
    public string FromToText => Selected is { } s ? $"v{CurrentVersion} → {s.Version}" + (s.SizeText.Length > 0 && !s.IsOnThisPc ? $" · {s.SizeText}" : "") : "";
    public string SourceText => Selected is { } s
        ? s.IsOnThisPc ? "Saved on this PC — no download needed." : s.CanInstall ? "Downloads from the official GitHub release." : ""
        : "";
    public string ContinueText => Selected is { } s ? (IsHistoryTab ? $"Roll back to {s.Version}" : $"Continue with {s.Version}") : "Select a version";

    public bool HasSettingsBackup => IsHistoryTab && Selected is { } s && _archive.SettingsBackup(s.Release.DisplayVersion) != null;
    public bool NoSettingsBackup => IsHistoryTab && Selected != null && !HasSettingsBackup;
    public bool ShowPin => IsHistoryTab;

    [ObservableProperty] private bool _restoreSettings = true;
    public bool KeepCurrentSettings { get => !RestoreSettings; set { if (value) RestoreSettings = false; } }
    partial void OnRestoreSettingsChanged(bool value) => OnPropertyChanged(nameof(KeepCurrentSettings));

    [ObservableProperty] private bool _pin = true;
    [ObservableProperty] private bool _backUpSettings = true;
    [ObservableProperty] private bool _keepCurrentCopy;
    public string KeepCurrentText => $"Keep v{CurrentVersion} on this PC so I can come back to it";

    // ───────────── confirm step ─────────────

    [ObservableProperty] private bool _isConfirming;
    partial void OnIsConfirmingChanged(bool value) => OnPropertyChanged(nameof(IsPicking));
    public bool IsPicking => !IsConfirming;

    public VersionChangeRequest? Request => Selected is { } s
        ? new VersionChangeRequest(s.Release, CurrentVersion, InstalledMode, BackUpSettings, KeepCurrentCopy, s.IsOnThisPc,
            RestoreSettings, HasSettingsBackup, Pin)
        : null;

    public string ConfirmTitle => Request?.IsRollBack == true ? "Confirm roll back" : "Confirm update";
    public string FromDetail => KeepCurrentCopy ? "A copy stays on this PC, so you can come back to it." : "No copy is kept on this PC.";
    public string ToVersion => Selected?.Version ?? "";
    public string ToDetail => Selected is { } s ? s.DateText + (s.SizeText.Length > 0 ? " · " + s.SizeText : "") : "";
    public string? ConfirmWarning => Request is { } r ? VersionChange.Warning(r, _releases) : null;
    public IReadOnlyList<string> Steps => Request is { } r ? VersionChange.Steps(r).Select((t, i) => $"{i + 1}.  {t}").ToList() : Array.Empty<string>();
    public string InstallText => Request is { } r ? (r.IsRollBack ? $"Roll back to {ToVersion}" : $"Update to {ToVersion}") : "";

    [RelayCommand(CanExecute = nameof(CanContinue))]
    private void Continue()
    {
        if (Selected is null) return;
        Error = null;
        IsConfirming = true;
        foreach (var name in new[] { nameof(Request), nameof(ConfirmTitle), nameof(FromDetail), nameof(ToVersion), nameof(ToDetail), nameof(ConfirmWarning), nameof(Steps), nameof(InstallText) })
            OnPropertyChanged(name);
    }

    [RelayCommand] private void Back() { if (!IsBusy) IsConfirming = false; }

    // ───────────── doing the change ─────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _error;

    public event Action? RequestClose;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (IsBusy || Request is not { } request || Selected?.Asset is not { } asset) return;
        var target = request.Target;
        var fromVersion = CurrentVersion;
        var toVersion = target.DisplayVersion;
        var startedUtc = DateTime.UtcNow;
        var output = new List<string> { $"{(request.IsRollBack ? "Rolling back" : "Updating")} Evict {fromVersion} → {toVersion} ({target.TagName}), chosen in the version picker" };
        bool handedOver = false, settingsPutBack = false;
        Error = null;
        IsBusy = true;
        IsIndeterminate = true;
        // Evict's own update goes into the Software Updater history (design SH1, choice E1).
        void RecordFailure(string reason)
        {
            try { _services.UpdateHistory.FailSelfUpdate(fromVersion, toVersion, target.TagName, "You", startedUtc, reason, output); }
            catch (Exception ex) { Log.Warn("Could not record the failed version change: " + ex.Message); }
        }
        try
        {
            // 1. Settings of the version you have now.
            _services.Settings.Save();
            if (request.BackUpSettings)
            {
                StatusText = "Saving your settings…";
                output.Add(_archive.BackUpSettings(fromVersion, _services.Settings.FilePath)
                    ? $"Saved the settings used with {fromVersion}"
                    : "Could not save a copy of the settings (continuing)");
            }

            // 2. A copy of the version you have now, so you can come back to it.
            if (request.KeepCurrentCopy) await KeepCurrentVersionAsync(fromVersion, output, ct);

            // 3. The version you chose: the verified copy on this PC, otherwise a verified download.
            string path;
            var kept = request.TargetOnThisPc ? _archive.Find(toVersion, asset.Name) : null;
            if (kept != null)
            {
                StatusText = $"Using the copy of v{toVersion} saved on this PC…";
                path = Path.Combine(_services.Updater.UpdatesDir, $"{Path.GetFileNameWithoutExtension(asset.Name)}-{toVersion}-kept{Path.GetExtension(asset.Name)}");
                File.Copy(kept, path, overwrite: true);
                output.Add($"Used the copy of {toVersion} saved on this PC (checksum OK)");
            }
            else
            {
                if (request.TargetOnThisPc) output.Add($"The copy of {toVersion} on this PC no longer matches its checksum; downloading it instead");
                StatusText = "Connecting…";
                var progress = new Progress<(long Done, long Total)>(p =>
                {
                    if (p.Total > 0)
                    {
                        IsIndeterminate = false;
                        Progress = Math.Min(100, p.Done * 100.0 / p.Total);
                        StatusText = $"Downloading v{toVersion}… {SizeFormatter.Format(p.Done)} of {SizeFormatter.Format(p.Total)}";
                    }
                    else StatusText = $"Downloading v{toVersion}… {SizeFormatter.Format(p.Done)}";
                });
                path = await _services.Updater.DownloadAsync(target, asset, progress, ct);
                output.Add($"Downloaded {asset.Name} and checked it against the release");
            }

            // 4. Remember the choice; put back the older settings for a roll back.
            IsIndeterminate = true;
            VersionChange.ApplyChoices(S, request);
            _services.Settings.Save();
            if (request.WillRestoreSettings)
            {
                settingsPutBack = _archive.RestoreSettings(toVersion, _services.Settings.FilePath, VersionChange.CarriedSettings(S));
                if (settingsPutBack)
                {
                    _services.Settings.SuspendSaves(); // this closing instance must not overwrite them
                    output.Add($"Put back the settings used with {toVersion}");
                }
            }
            if (request.WillPin) output.Add($"Pinned {toVersion}");
            _archive.Prune(3, new[] { fromVersion, toVersion });

            // 5. Hand over.
            StatusText = InstalledMode ? "Starting the installer…" : "Replacing Evict.exe…";
            await Task.Delay(300, ct);
            output.Add(InstalledMode ? "Started the installer; Evict closes and starts again" : "Swapped Evict.exe; Evict starts again");
            _services.UpdateHistory.BeginSelfUpdate(fromVersion, toVersion, target.TagName, "You", output);
            handedOver = true;

            var (ok, error) = _services.Updater.Apply(path, InstalledMode, beforeRestart: Program.ReleaseSingleInstance,
                restartFailed: () =>
                {
                    if (Program.RestoreSingleInstance()) return;
                    App.Quit();
                    throw new InvalidOperationException("Another Evict instance opened during the failed version change. This instance will close; reopen Evict to continue.");
                });
            if (ok)
            {
                _ = Application.Current.Dispatcher.BeginInvoke(App.Quit);
                return;
            }
            UndoSettingsPutBack(settingsPutBack);
            Error = error;
            RecordFailure(error ?? "The version change could not be applied");
        }
        catch (OperationCanceledException) when (!handedOver)
        {
            UndoSettingsPutBack(settingsPutBack);
            StatusText = "Cancelled — nothing changed.";
            try { _services.UpdateHistory.CancelSelfUpdate(fromVersion, toVersion, target.TagName, "You", startedUtc, output); }
            catch (Exception ex) { Log.Warn("Could not record the cancelled version change: " + ex.Message); }
        }
        catch (Exception ex)
        {
            UndoSettingsPutBack(settingsPutBack);
            Log.Error("Version change failed", ex);
            Error = ex.Message;
            RecordFailure(ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>The roll back did not happen: write this version's settings again over the older ones.</summary>
    private void UndoSettingsPutBack(bool settingsPutBack)
    {
        if (!settingsPutBack) return;
        _services.Settings.ResumeSaves();
        _services.Settings.Save();
    }

    private async Task KeepCurrentVersionAsync(string version, List<string> output, CancellationToken ct)
    {
        try
        {
            if (!InstalledMode)
            {
                StatusText = $"Keeping a copy of v{version}…";
                var exe = UpdateService.ExePath;
                _archive.Keep(version, exe, "Evict.exe", VersionArchive.Sha256(exe));
                output.Add($"Kept a copy of Evict {version} on this PC");
                return;
            }
            var current = VersionPicker.Distinct(_releases).FirstOrDefault(r => r.DisplayVersion == UpdateVersion.Parse(version)?.ToString());
            var setup = current is null ? null : UpdateChecker.PickAsset(current, installedMode: true);
            if (current is null || setup is null) { output.Add($"Evict {version} is not on GitHub, so no copy was kept"); return; }
            if (_archive.Find(version, setup.Name) != null) { output.Add($"A copy of Evict {version} is already on this PC"); return; }
            StatusText = $"Keeping a copy of v{version} (downloading its installer)…";
            var file = await _services.Updater.DownloadAsync(current, setup, null, ct);
            _archive.Keep(version, file, setup.Name, VersionArchive.Sha256(file));
            output.Add($"Kept the installer of Evict {version} on this PC");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Could not keep the current version: " + ex.Message);
            output.Add($"Could not keep a copy of Evict {version} (continuing): {ex.Message}");
        }
    }

    // ───────────── other buttons ─────────────

    [RelayCommand] private void OpenReleasePage() => Dialogs.OpenUrl(Selected?.Release.HtmlUrl ?? UpdateChecker.ReleasesUrl);
    [RelayCommand] private void OpenAllReleases() => Dialogs.OpenUrl(UpdateChecker.ReleasesUrl);

    /// <summary>"Don't remind me about these": the notice stays hidden until a version newer than the newest one shown appears.</summary>
    [RelayCommand]
    private void SkipThese()
    {
        if (VersionPicker.Newest(VersionPicker.Newer(_releases, CurrentVersion)) is { } newest)
        {
            S.SkippedUpdateVersion = newest.TagName;
            _services.Settings.Save();
            _main.RefreshOffer();
        }
        RequestClose?.Invoke();
    }

    [RelayCommand] private void Close() { if (IsBusy) InstallCancelCommand.Execute(null); else RequestClose?.Invoke(); }
}
