using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Formatting;
using DeployNest.Core.Installation;
using DeployNest.Core.Linux;
using DeployNest.Core.Settings;
using DeployNest.Infrastructure.Catalog;
using DeployNest.Infrastructure.Linux;
using DeployNest.Linux.ViewModels;
using Microsoft.Extensions.Logging;

namespace DeployNest.Linux.Services;

/// <summary>
/// Application-wide state of the Linux edition: the Linux part of the catalog, the selection
/// (kept across pages), the detected system and installed-state detection.
/// </summary>
public sealed partial class LinuxCatalogState(
    CatalogProvider catalogProvider,
    LinuxSystemDetector systemDetector,
    LinuxSystemInfoProvider systemInfo,
    LinuxInventorySource inventorySource,
    LinuxInstallationDetector detector,
    PrivilegedSessionProvider elevation,
    FlatpakClient flatpak,
    ISettingsStore settings,
    ILogger<LinuxCatalogState> logger) : ObservableObject
{
    private readonly SemaphoreSlim _detectionGate = new(1, 1);

    public SoftwareCatalog Catalog { get; private set; } = null!;
    public int RejectedEntries { get; private set; }
    public ObservableCollection<SoftwareItemViewModel> Items { get; } = [];
    public ObservableCollection<SoftwareItemViewModel> Selected { get; } = [];
    public IReadOnlyList<CategoryViewModel> Categories { get; private set; } = [];
    public IReadOnlyList<PresetViewModel> Presets { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemText), nameof(PackageManagerText), nameof(FlatpakText), nameof(FlatpakMissing), nameof(FlatpakReady))]
    private LinuxSystemInfo? _system;

    [ObservableProperty]
    private bool _isDetecting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledText))]
    private int _installedCount;

    [ObservableProperty]
    private bool _isInstallingFlatpak;

    [ObservableProperty]
    private string? _flatpakMessage;

    public string SystemText => System?.Distribution.PrettyName ?? Strings.Get("Linux.Status.Detecting");

    public string PackageManagerText => System switch
    {
        null => string.Empty,
        { PackageManager: null } => Strings.Get("Linux.Status.NoPackageManager"),
        var s => Strings.Format("Linux.Status.PackageManager", s.PackageManagerName),
    };

    public string FlatpakText => System switch
    {
        null => string.Empty,
        { FlatpakAvailable: true } s => Strings.Format("Linux.Status.Flatpak", s.FlatpakVersion),
        _ => Strings.Get("Linux.Status.NoFlatpak"),
    };

    public bool FlatpakReady => System?.FlatpakAvailable == true;

    public bool FlatpakMissing => System is { FlatpakAvailable: false };

    public string InstalledText => $"{InstalledCount} {Strings.Get("Home.InstalledDetected")}";

    public int SelectedCount => Selected.Count;

    public string SelectionText => SelectedCount == 0
        ? Strings.Get("Linux.Selection.None")
        : Strings.Format("Linux.Selection.Count", SelectedCount);

    public event EventHandler? SelectionChanged;

    public void Initialize()
    {
        var loaded = catalogProvider.Load();
        if (!loaded.Result.IsUsable)
            throw new InvalidOperationException("Program kataloğu yüklenemedi: " + string.Join("; ", loaded.Result.Issues.Select(i => i.Message)));

        RejectedEntries = loaded.Result.RejectedEntryCount;
        Catalog = loaded.Result.Catalog!.ForPlatform(CatalogPlatform.Linux);
        foreach (var entry in Catalog.Software)
        {
            var category = Catalog.FindCategory(entry.CategoryId)!.Name.Get("tr");
            Items.Add(new SoftwareItemViewModel(entry, category, OnItemSelectionChanged));
        }

        Categories = Catalog.Categories
            .Select(c => new CategoryViewModel(c.Id, c.Name.Get("tr"), CategoryViewModel.IconFor(c.Id), Catalog.Software.Count(s => s.CategoryId == c.Id)))
            .ToList();
        Presets = Catalog.Presets
            .Select(p => new PresetViewModel(p, p.SoftwareIds.Select(id => Catalog.FindSoftware(id)!.Name).ToList()))
            .ToList();
    }

    public SoftwareItemViewModel? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    /// <summary>Selects the programs that can be installed on this system; returns how many were newly selected.</summary>
    public int SelectMany(IEnumerable<string> ids)
    {
        var count = 0;
        foreach (var id in ids)
        {
            if (Find(id) is { IsAvailable: true, IsSelected: false } item)
            {
                item.IsSelected = true;
                count++;
            }
        }
        return count;
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
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Detects the distribution, package manager and Flatpak, then the installed programs.</summary>
    public async Task DetectSystemAsync()
    {
        var info = await systemDetector.DetectAsync(CancellationToken.None);
        systemInfo.Current = info;
        System = info;
        foreach (var item in Items)
            item.ApplyAvailability(info.PackageManager);
        await RefreshDetectionAsync();
    }

    public async Task RefreshDetectionAsync()
    {
        if (System is not { } system || !await _detectionGate.WaitAsync(0))
            return;

        IsDetecting = true;
        try
        {
            foreach (var item in Items)
                item.State = InstallState.Checking;
            var inventory = await inventorySource.ReadAsync(system, CancellationToken.None);
            foreach (var item in Items)
            {
                var result = detector.Evaluate(item.Entry, inventory);
                item.State = result.State;
                item.InstalledVersion = result.Version;
                item.Evidence = result.Evidence;
            }
            InstalledCount = Items.Count(i => i.IsInstalled);
        }
        finally
        {
            IsDetecting = false;
            _detectionGate.Release();
        }
    }

    /// <summary>Checks after an installation whether the program is now detected.</summary>
    public async Task<bool?> IsInstalledAsync(SoftwareEntry entry, CancellationToken cancellationToken)
    {
        if (System is not { } system)
            return null;
        var inventory = await inventorySource.ReadAsync(system, cancellationToken);
        return detector.Evaluate(entry, inventory).State switch
        {
            InstallState.Installed => true,
            InstallState.NotInstalled => false,
            _ => null,
        };
    }

    public PlannerContext CreatePlannerContext() =>
        PlannerContext.Linux(System?.FlatpakAvailable == true, System?.PackageManager, settings.Load().PreferNativePackages);

    /// <summary>Installs the flatpak package from the distribution repository (one password prompt).</summary>
    public async Task InstallFlatpakAsync()
    {
        if (IsInstallingFlatpak)
            return;
        IsInstallingFlatpak = true;
        FlatpakMessage = Strings.Get("Linux.Flatpak.Installing");
        try
        {
            var (status, session, detail) = await elevation.GetAsync(CancellationToken.None);
            if (session is null)
            {
                FlatpakMessage = Strings.Format("Linux.Flatpak.Failed", detail ?? status.ToString());
                return;
            }

            var result = await session.InstallAsync([], installFlatpak: true, null, CancellationToken.None);
            FlatpakMessage = result.ExitCode == 0
                ? Strings.Get("Linux.Flatpak.Installed")
                : Strings.Format("Linux.Flatpak.Failed", FlatpakClient.LastLine(result.StandardOutput) ?? $"exit {result.ExitCode}");
            if (result.ExitCode == 0)
                await flatpak.EnsureRemoteAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Flatpak kurulamadı");
            FlatpakMessage = Strings.Format("Linux.Flatpak.Failed", ex.Message);
        }
        finally
        {
            await elevation.ResetAsync();
            IsInstallingFlatpak = false;
            await DetectSystemAsync();
        }
    }

    public static string FormatSize(long bytes) => SizeFormatter.Format(bytes, Strings.Localizer.Culture);
}
