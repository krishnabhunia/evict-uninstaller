using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

/// <summary>One "SrNo. | Item | Value" row on the This build and Under the hood tabs.</summary>
public sealed record InfoRow(int SrNo, string Item, string Value, string? Url = null)
{
    public bool HasUrl => Url is not null;
}

/// <summary>
/// The Info window (design IN1): This build · What's implemented · What's new · Under the hood. All parsing and wording
/// lives in Evict.Core (BuildInfo, InfoContent); this view model only connects it to the window.
/// </summary>
public sealed partial class InfoViewModel : ObservableObject
{
    public const int ThisBuildTab = 0, FeaturesTab = 1, WhatsNewTab = 2, UnderTheHoodTab = 3;

    private readonly IReadOnlyList<FeatureRow> _features;

    public InfoViewModel(BuildInfo build, string? featuresMarkdown, string? changelogMarkdown, bool isDarkTheme, double textScale, int initialTab = ThisBuildTab)
    {
        Build = build;
        _selectedTab = Math.Clamp(initialTab, ThisBuildTab, UnderTheHoodTab);
        BuildRows = build.Rows().Select((row, i) => new InfoRow(i + 1, row.Item, row.Value,
            row.Item == "Made from" ? build.PullRequestUrl : null)).ToList();
        _features = InfoContent.ParseFeatures(featuresMarkdown);
        Changelog = InfoContent.ChangelogFor(changelogMarkdown, build.CoreVersion);
        StackRows = new List<(string, string)>
        {
            ("App", $"{BuildInfo.AppName} for Windows, written in C# on .NET {Environment.Version.ToString(3)} ({(Environment.Is64BitProcess ? "64-bit" : "32-bit")})"),
            ("User interface", "WPF with CommunityToolkit.Mvvm; system fonts Segoe UI and Segoe Fluent Icons (no bundled fonts)"),
            ("Packaging", "One self-contained Evict.exe (portable) and an Inno Setup installer"),
            ("Storage", $"Settings, history, install logs and the log file as local files in {AppPaths.DataRoot}"),
            ("Windows access", "Registry, WMI, services, Task Scheduler, Microsoft Store packages and winget"),
            ("Updates", "GitHub Releases of krishnabhunia/evict-uninstaller, SHA-256 checked before installing; "
                        + $"automatic check {(App.Services.Settings.Current.CheckForUpdates ? "on" : "off")}, "
                        + $"test versions (betas) {(App.Services.Settings.Current.IncludeBetaUpdates ? "on" : "off")}"),
            ("Design", $"{(isDarkTheme ? "Dark" : "Light")} theme, text size {Math.Round(textScale * 100)} %"),
        }.Select((row, i) => new InfoRow(i + 1, row.Item1, row.Item2)).ToList();
    }

    public BuildInfo Build { get; }
    public string Title => $"{BuildInfo.AppName} · Info";
    public string Subtitle => Build.Subtitle();
    [ObservableProperty] private int _selectedTab;

    // ── This build ──
    public string? BetaBanner => Build.BetaBanner;
    public bool HasBetaBanner => BetaBanner is not null;
    public IReadOnlyList<InfoRow> BuildRows { get; }

    // ── What's implemented ──
    [ObservableProperty] private string _featureQuery = "";
    [ObservableProperty] private bool _showNewOnly;
    partial void OnFeatureQueryChanged(string value) => OnPropertyChanged(nameof(FilteredFeatures));
    partial void OnShowNewOnlyChanged(bool value) { OnPropertyChanged(nameof(FilteredFeatures)); OnPropertyChanged(nameof(ShowAllAreas)); }
    public bool ShowAllAreas { get => !ShowNewOnly; set { if (value) ShowNewOnly = false; } }
    public string NewInLabel => $"New in {Build.CoreVersion}";
    public IReadOnlyList<FeatureRow> FilteredFeatures =>
        InfoContent.FilterFeatures(_features, FeatureQuery, ShowNewOnly ? FeatureFilter.NewInThisVersion : FeatureFilter.All, Build.CoreVersion, mac: false);
    public string FeatureCountText => _features.Count == 0 ? "The feature list is not part of this build." : $"{_features.Count(f => f.OnWindows)} features in this copy.";

    // ── What's new ──
    public ChangelogSection? Changelog { get; }
    public bool HasChangelog => Changelog is not null;
    public string WhatsNewHeading => Changelog is null ? $"Version {Build.CoreVersion}" : $"Version {Changelog.Heading}";
    public string WhatsNewText => Changelog?.Text ?? "This build has no changelog section for its version yet.";
    public string? WhatsNewNote => Build.IsBeta ? (Build.BetaNumber is { } n ? $"beta {n} · being tested" : "test build · being tested") : null;
    public bool HasWhatsNewNote => WhatsNewNote is not null;

    // ── Under the hood ──
    public IReadOnlyList<InfoRow> StackRows { get; }

    [ObservableProperty] private string? _statusText;

    [RelayCommand] private void OpenUrl(string? url) { if (!string.IsNullOrEmpty(url)) Dialogs.OpenUrl(url); }
    [RelayCommand] private void OpenReleases() => Dialogs.OpenUrl(UpdateChecker.ReleasesUrl);
    [RelayCommand] private void OpenDataFolder() => Dialogs.OpenFolder(AppPaths.DataRoot);
    [RelayCommand] private void OpenLogFile() => Dialogs.OpenFolder(AppPaths.LogFile);

    [RelayCommand]
    private void CopyBuildInfo()
    {
        try
        {
            Clipboard.SetText(Build.CopyText(DescribeWindows(), BuildInfo.DescribeRuntime(Environment.Version)));
            StatusText = "Build info copied.";
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another program for a moment.
            Log.Warn("Copy build info failed: " + ex.Message);
            StatusText = "Could not copy – try again.";
        }
    }

    private static string DescribeWindows()
    {
        string? product = null, display = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = key?.GetValue("ProductName") as string;
            display = key?.GetValue("DisplayVersion") as string;
        }
        catch { /* the plain OS version below is enough */ }
        return BuildInfo.DescribeWindows(product, display, Environment.OSVersion.Version.Build, Environment.Is64BitOperatingSystem);
    }

    /// <summary>Creates the view model for the running copy, with the feature list and changelog embedded in Evict.exe.</summary>
    public static InfoViewModel ForThisCopy(int initialTab)
    {
        var build = BuildInfo.FromAssembly(typeof(BuildInfo).Assembly, App.Services.Updater.CurrentVersionLabel, UpdateService.InstallKindText());
        return new InfoViewModel(build, ReadResource("Evict.Info.FEATURES.md"), ReadResource("Evict.Info.CHANGELOG.md"),
            App.Services.Settings.Current.Theme == "Dark", App.UiState.Scale, initialTab);
    }

    private static string? ReadResource(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            Log.Warn($"Info resource {name} unavailable: {ex.Message}");
            return null;
        }
    }
}
