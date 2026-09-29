using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

/// <summary>Row model for the Programs grid.</summary>
public sealed partial class ProgramItemViewModel : ObservableObject
{
    private readonly IconProvider _icons;
    private bool _iconRequested;

    public ProgramItemViewModel(InstalledProgram program, IconProvider icons)
    {
        Program = program;
        _icons = icons;
    }

    public InstalledProgram Program { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _icon;

    public string Name => Program.DisplayName;
    public string Publisher => Program.Publisher ?? "—";
    public string Version => Program.DisplayVersion ?? "—";
    public long SizeSort => Program.SizeBytes ?? -1;
    public string SizeText => Program.SizeBytes is null ? "—" : SizeFormatter.Format(Program.SizeBytes) + (Program.SizeIsMeasured ? "" : "");
    public DateTime InstallDateSort => Program.EffectiveInstallDate ?? DateTime.MinValue;
    public string InstallDateText => Program.EffectiveInstallDate is { } d ? d.ToString("dd MMM yyyy") : "—";
    public DateTime LastUsedSort => Program.LastUsed ?? DateTime.MinValue;
    public string LastUsedText => Program.LastUsed is { } d ? Relative(d) : (Program.RunCount > 0 ? "Unknown" : "Never seen");
    public string ArchitectureText => Program.IsPerUser ? "Per-user" : Program.Is32Bit ? "32-bit" : "64-bit";
    public string InstallerText => Program.IsMsi ? "Windows Installer (MSI)" : Program.Installer switch
    {
        InstallerKind.InnoSetup => "Inno Setup",
        InstallerKind.Nsis => "NSIS",
        InstallerKind.InstallShield => "InstallShield",
        InstallerKind.WiseInstaller => "Wise",
        InstallerKind.SquirrelOrElectron => "Squirrel / Electron",
        InstallerKind.Other => "Custom uninstaller",
        _ => "Unknown",
    };
    public string InstallLocationText => Program.InstallLocation ?? "—";
    public string UninstallStringText => Program.UninstallString ?? Program.QuietUninstallString ?? "—";
    public bool IsBundleSuspect => Program.IsBundleSuspect;
    public bool IsKnownBundleware => Program.IsKnownBundleware;
    public bool IsBroken => Program.IsBrokenEntry;
    public bool HasUninstaller => Program.HasUninstaller;
    public string? BundleNote => Program.BundleGroupNote;
    public bool HasInstallLocation => !string.IsNullOrEmpty(Program.InstallLocation) && Directory.Exists(Program.InstallLocation);
    public bool HasWebsite => !string.IsNullOrWhiteSpace(Program.UrlInfoAbout) || !string.IsNullOrWhiteSpace(Program.HelpLink);
    public string StatusGlyph => IsBroken ? "" : IsBundleSuspect ? "" : "";
    public string StatusTooltip => IsBroken ? "Broken entry: install folder and uninstaller are missing" : IsBundleSuspect ? (BundleNote ?? "Possibly bundled software") : "";

    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    /// <summary>Lazily loads the icon the first time a row is rendered.</summary>
    public void EnsureIcon()
    {
        if (_iconRequested) return;
        _iconRequested = true;
        _ = Task.Run(() =>
        {
            var img = _icons.GetIcon(Program);
            if (img != null) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Icon = img);
        });
    }

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeSort));
        OnPropertyChanged(nameof(LastUsedText));
        OnPropertyChanged(nameof(IsBundleSuspect));
        OnPropertyChanged(nameof(IsKnownBundleware));
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(StatusTooltip));
    }

    internal static string Relative(DateTime d)
    {
        var span = DateTime.Now - d;
        if (span.TotalMinutes < 1) return "Just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "Yesterday";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} months ago";
        return d.ToString("dd MMM yyyy");
    }

    public bool Matches(string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (Program.Publisher?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Program.DisplayVersion?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (Program.InstallLocation?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
