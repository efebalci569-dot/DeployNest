using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.App.ViewModels;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Formatting;
using DeployNest.Core.Localization;
using DeployNest.Infrastructure.Catalog;
using DeployNest.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.Services;

/// <summary>
/// Application-wide state: the loaded catalog, the user's selection (kept across pages) and
/// installed-state detection results.
/// </summary>
public sealed partial class CatalogState : ObservableObject
{
    private readonly CatalogProvider _catalogProvider;
    private readonly InstallationDetector _detector;
    private readonly IInstalledProgramSource _registry;
    private readonly IWingetInventorySource _wingetInventory;
    private readonly IWingetLocator _wingetLocator;
    private readonly IconService _icons;
    private readonly PackageInfoCache _cache;
    private readonly ILocalizer _loc;
    private readonly ILogger<CatalogState> _logger;
    private readonly SemaphoreSlim _detectionGate = new(1, 1);

    public CatalogState(
        CatalogProvider catalogProvider,
        InstallationDetector detector,
        IInstalledProgramSource registry,
        IWingetInventorySource wingetInventory,
        IWingetLocator wingetLocator,
        IconService icons,
        PackageInfoCache cache,
        ILocalizer localizer,
        ILogger<CatalogState> logger)
    {
        _catalogProvider = catalogProvider;
        _detector = detector;
        _registry = registry;
        _wingetInventory = wingetInventory;
        _wingetLocator = wingetLocator;
        _icons = icons;
        _cache = cache;
        _loc = localizer;
        _logger = logger;
    }

    public SoftwareCatalog Catalog { get; private set; } = null!;
    public IReadOnlyList<CatalogIssue> CatalogIssues { get; private set; } = [];
    public bool CatalogUsedFallback { get; private set; }
    public string CatalogSource { get; private set; } = "embedded";
    public ObservableCollection<SoftwareItemViewModel> Items { get; } = [];
    public ObservableCollection<SoftwareItemViewModel> Selected { get; } = [];
    public IReadOnlyList<CategoryViewModel> Categories { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WingetAvailable), nameof(WingetStatusText))]
    private WingetStatus? _wingetStatus;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    private int _installedCount;

    [ObservableProperty]
    private DateTimeOffset? _lastDetection;

    public bool WingetAvailable => WingetStatus?.Available == true;

    public string WingetStatusText => WingetStatus switch
    {
        null => _loc["Winget.Checking"],
        { Available: true, Version: var v } => _loc.Format("Winget.Ready", v),
        { Problem: "tooOld", Version: var v } => _loc.Format("Winget.TooOld", v),
        _ => _loc["Winget.Missing"],
    };

    public int SelectedCount => Selected.Count;

    public long SelectedKnownBytes => Selected.Sum(i => i.SizeBytes ?? 0);

    public int SelectedUnknownSizeCount => Selected.Count(i => i.SizeBytes is not > 0);

    public string SelectedSizeText => SelectedCount == 0
        ? string.Empty
        : SelectedKnownBytes == 0
            ? _loc["Selection.SizeUnknown"]
            : SelectedUnknownSizeCount == 0
                ? "~" + SizeFormatter.Format(SelectedKnownBytes, _loc.Culture)
                : _loc.Format("Selection.SizePartial", SizeFormatter.Format(SelectedKnownBytes, _loc.Culture), SelectedUnknownSizeCount);

    public event EventHandler? SelectionChanged;
    public event EventHandler? DetectionUpdated;

    public void Initialize()
    {
        var loaded = _catalogProvider.Load();
        if (!loaded.Result.IsUsable)
        {
            // The embedded catalog is validated by tests; reaching this means the build is broken.
            throw new InvalidOperationException("Program kataloğu yüklenemedi: " +
                string.Join("; ", loaded.Result.Issues.Select(i => i.Message)));
        }

        // The catalog is shared with the Linux edition; only programs installable on Windows are shown.
        Catalog = loaded.Result.Catalog!.ForPlatform(CatalogPlatform.Windows);
        CatalogIssues = loaded.Result.Issues;
        CatalogUsedFallback = loaded.UsedFallback;
        CatalogSource = loaded.Source;

        foreach (var entry in Catalog.Software)
        {
            var category = Catalog.FindCategory(entry.CategoryId)!.Name.Get(_loc.Language);
            var item = new SoftwareItemViewModel(entry, category, _loc, OnItemSelectionChanged)
            {
                Icon = _icons.LoadBundledLogo(entry.Id),
            };
            if (_cache.Get(entry.Id) is { } cached)
            {
                item.SizeBytes = cached.SizeBytes;
                item.LatestVersion = cached.Version;
            }
            Items.Add(item);
        }

        Categories = Catalog.Categories
            .Select(c => new CategoryViewModel(c.Id, c.Name.Get(_loc.Language), c.IconGlyph, Catalog.Software.Count(s => s.CategoryId == c.Id)))
            .ToList();
    }

    public SoftwareItemViewModel? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public void SelectMany(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (Find(id) is { } item)
                item.IsSelected = true;
        }
    }

    public void ClearSelection()
    {
        foreach (var item in Selected.ToList())
            item.IsSelected = false;
    }

    private void OnItemSelectionChanged(SoftwareItemViewModel item)
    {
        if (item.IsSelected && !Selected.Contains(item))
            Selected.Add(item);
        else if (!item.IsSelected)
            Selected.Remove(item);
        NotifySelection();
    }

    public void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedKnownBytes));
        OnPropertyChanged(nameof(SelectedSizeText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads the registry first (fast) and the winget inventory second (slower), updating the
    /// cards after each step. Safe to call repeatedly; concurrent calls are serialized.
    /// </summary>
    public async Task RefreshDetectionAsync()
    {
        if (!await _detectionGate.WaitAsync(0))
            return;

        IsDetecting = true;
        try
        {
            foreach (var item in Items)
                item.State = InstallState.Checking;

            var statusTask = _wingetLocator.GetStatusAsync(CancellationToken.None);
            IReadOnlyList<InstalledProgram>? registry = null;
            var registryFailed = false;
            try
            {
                registry = await _registry.GetInstalledProgramsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                registryFailed = true;
                _logger.LogWarning(ex, "Kayıt defteri okunamadı");
            }

            Apply(new DetectionSnapshot(registry, registryFailed, null));

            WingetStatus = await statusTask;
            var inventory = await _wingetInventory.GetInventoryAsync(CancellationToken.None);
            Apply(new DetectionSnapshot(registry, registryFailed, inventory));
            LastDetection = DateTimeOffset.Now;
        }
        finally
        {
            IsDetecting = false;
            _detectionGate.Release();
            DetectionUpdated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Apply(DetectionSnapshot snapshot)
    {
        foreach (var item in Items)
        {
            var result = _detector.Evaluate(item.Entry, snapshot);
            item.State = result.State;
            item.InstalledVersion = result.Version;
            item.Evidence = result.Evidence;
            if (result.IconPath is { } iconPath && item.Icon is null)
                _ = LoadIconAsync(item, iconPath);
        }
        InstalledCount = Items.Count(i => i.State == InstallState.Installed);
        DetectionUpdated?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadIconAsync(SoftwareItemViewModel item, string iconPath)
    {
        var icon = await _icons.LoadAsync(iconPath);
        if (icon is not null)
            item.Icon = icon;
    }

    /// <summary>Returns whether winget is usable, detecting it first if that has not happened yet.</summary>
    public async Task<bool> WingetStatusOrDetectAsync()
    {
        WingetStatus ??= await _wingetLocator.GetStatusAsync(CancellationToken.None);
        return WingetAvailable;
    }

    public void UpdatePackageInfo(SoftwareItemViewModel item, string? version, long? size)
    {
        if (size is > 0)
            item.SizeBytes = size;
        if (version is not null)
            item.LatestVersion = version;
        if (item.IsSelected)
            NotifySelection();
    }
}
