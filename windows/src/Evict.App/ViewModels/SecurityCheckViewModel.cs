using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

public sealed class SecurityFindingViewModel
{
    public SecurityFindingViewModel(SecurityFinding f) => Finding = f;
    public SecurityFinding Finding { get; }
    public string Section => Finding.Section;
    public string Title => Finding.Title;
    public string Detail => Finding.Detail;
    public string SeverityText => Finding.Severity switch { FindingSeverity.High => "high", FindingSeverity.Medium => "review", _ => "info" };
    public bool IsHigh => Finding.Severity == FindingSeverity.High;
    public bool IsMedium => Finding.Severity == FindingSeverity.Medium;
    public bool HasAction => Finding.ActionKey != null;
    public string ActionText => Finding.ActionKey switch
    {
        "windowsSecurity" => "Open Windows Security",
        "quickscan" => "Run quick scan",
        "extensions" => "Browser Extensions",
        "startup" => "Startup Apps",
        _ => "",
    };
}

/// <summary>"Malicious software &amp; extensions": Defender state and threats, suspicious extensions, unsigned startup programs.</summary>
public sealed partial class SecurityCheckViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Action<string> _navigate;

    /// <param name="navigate">Opens another part of Evict for an action ("extensions", "startup").</param>
    public SecurityCheckViewModel(AppServices services, IEnumerable<SecurityFinding> findings, Action<string> navigate)
    {
        _services = services;
        _navigate = navigate;
        View = CollectionViewSource.GetDefaultView(Findings);
        View.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SecurityFindingViewModel.Section)));
        Load(findings);
    }

    public ObservableCollection<SecurityFindingViewModel> Findings { get; } = new();
    public ICollectionView View { get; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    public bool AnythingChanged { get; private set; }
    public string Summary => Findings.Count(f => f.IsHigh || f.IsMedium) is var n && n == 0 ? "Nothing suspicious found." : $"{n} item(s) need a look.";
    public event Action? RequestClose;

    private void Load(IEnumerable<SecurityFinding> findings)
    {
        Findings.Clear();
        foreach (var f in findings.OrderBy(f => f.Severity)) Findings.Add(new SecurityFindingViewModel(f));
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private async Task RecheckAsync()
    {
        IsBusy = true;
        StatusText = "Checking…";
        try { Load(await new SecurityCheckService().CheckAsync(_services.Browser, _services.Startup, CancellationToken.None)); StatusText = ""; }
        catch (Exception ex) { StatusText = "Check failed: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task QuickScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Microsoft Defender quick scan running – this takes a few minutes…";
        var err = await new SecurityCheckService().QuickScanAsync(CancellationToken.None);
        IsBusy = false;
        AnythingChanged = true;
        StatusText = err is null ? "Quick scan finished." : "Quick scan failed: " + err;
        if (err is null) await RecheckAsync();
    }

    [RelayCommand]
    private async Task ActAsync(SecurityFindingViewModel? f)
    {
        switch (f?.Finding.ActionKey)
        {
            case "windowsSecurity": Dialogs.OpenUrl("windowsdefender://threat/"); break;
            case "quickscan": await QuickScanAsync(); break;
            case "extensions" or "startup": RequestClose?.Invoke(); _navigate(f.Finding.ActionKey); break;
        }
    }

    [RelayCommand] private void OpenWindowsSecurity() => Dialogs.OpenUrl("windowsdefender:");
    [RelayCommand] private void Close() { if (!IsBusy) RequestClose?.Invoke(); }
}
