using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.App.Controls;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Formatting;
using DeployNest.Core.Localization;

namespace DeployNest.App.ViewModels;

/// <summary>A catalog program as shown in cards, lists and the details page.</summary>
public sealed partial class SoftwareItemViewModel : ObservableObject
{
    private readonly ILocalizer _loc;
    private readonly Action<SoftwareItemViewModel> _selectionChanged;

    public SoftwareItemViewModel(SoftwareEntry entry, string categoryName, ILocalizer localizer, Action<SoftwareItemViewModel> selectionChanged)
    {
        Entry = entry;
        CategoryName = categoryName;
        _loc = localizer;
        _selectionChanged = selectionChanged;
        AccentColor = (Color)ColorConverter.ConvertFromString(entry.Icon.Color);
    }

    public SoftwareEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string CategoryName { get; }
    public string Monogram => Entry.Icon.Monogram;
    public Color AccentColor { get; }
    public string Summary => Entry.Summary.Get(_loc.Language);
    public string Description => Entry.Description.Get(_loc.Language);
    public string License => Entry.License.Get(_loc.Language);
    public string? Notes => Entry.Notes?.Get(_loc.Language);
    public Uri? Homepage => Entry.Homepage;
    public Uri? LicenseUrl => Entry.LicenseUrl;
    public string? WingetId => Entry.Winget?.PackageId;
    public bool HasDirectFallback => Entry.Direct is not null;

    public string SourceText => Entry.Winget is { } w
        ? _loc.Format("Item.Source.Winget", w.PackageId)
        : _loc.Format("Item.Source.Direct", Entry.Direct!.Url.Host);

    public string ElevationText => _loc[$"Item.Elevation.{Entry.Elevation}"];

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StateKind), nameof(StateGlyph), nameof(IsInstalled))]
    private InstallState _state = InstallState.Checking;

    [ObservableProperty]
    private string? _installedVersion;

    [ObservableProperty]
    private string? _evidence;

    [ObservableProperty]
    private ImageSource? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(HasSize))]
    private long? _sizeBytes;

    [ObservableProperty]
    private string? _latestVersion;

    public bool IsInstalled => State == InstallState.Installed;

    public bool HasSize => SizeBytes is > 0;

    public string SizeText => SizeBytes is { } size and > 0
        ? "~" + SizeFormatter.Format(size, _loc.Culture)
        : _loc["Item.SizeUnknown"];

    public string StateText => State switch
    {
        InstallState.Installed => InstalledVersion is { Length: > 0 } v ? _loc.Format("State.InstalledVersion", v) : _loc["State.Installed"],
        InstallState.NotInstalled => _loc["State.NotInstalled"],
        InstallState.Unverifiable => _loc["State.Unverifiable"],
        _ => _loc["State.Checking"],
    };

    public PillKind StateKind => State switch
    {
        InstallState.Installed => PillKind.Success,
        InstallState.Unverifiable => PillKind.Warning,
        _ => PillKind.Neutral,
    };

    public string StateGlyph => State switch
    {
        InstallState.Installed => "",
        InstallState.Unverifiable => "",
        InstallState.Checking => "",
        _ => string.Empty,
    };

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);

    partial void OnInstalledVersionChanged(string? value) => OnPropertyChanged(nameof(StateText));

    public void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
    }
}

public sealed record CategoryViewModel(string? Id, string Name, string Glyph, int Count);
