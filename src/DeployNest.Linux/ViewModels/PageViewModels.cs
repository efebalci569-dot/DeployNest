using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Settings;
using DeployNest.Linux.Services;
using Avalonia;
using Avalonia.Styling;

namespace DeployNest.Linux.ViewModels;

/// <summary>Requests navigation from page view models without referencing the window.</summary>
public sealed class Navigator
{
    public Action<PageKey>? NavigateTo { get; set; }

    public void Go(PageKey key) => NavigateTo?.Invoke(key);
}

public sealed partial class HomeViewModel(LinuxCatalogState state, ProgramsViewModel programs, IHistoryStore history, Navigator navigator) : ObservableObject
{
    public LinuxCatalogState State => state;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _toast;

    public string RecentText
    {
        get
        {
            var entries = history.Load();
            if (entries.Count == 0)
                return Strings.Get("Home.Recent.Empty");
            var last = entries[0].SessionId;
            var session = entries.Where(e => e.SessionId == last).ToList();
            return Strings.Format("Home.Recent.Summary",
                session[0].Timestamp.LocalDateTime.ToString("g", Strings.Localizer.Culture),
                session.Count(e => e.Status == OutcomeStatus.Succeeded),
                session.Count(e => e.Status == OutcomeStatus.Failed),
                session.Count(e => e.Status is OutcomeStatus.Skipped or OutcomeStatus.Canceled));
        }
    }

    [RelayCommand]
    private void Search()
    {
        programs.ShowSearch(SearchText);
        navigator.Go(PageKey.Programs);
    }

    [RelayCommand]
    private void OpenCategory(CategoryViewModel category)
    {
        programs.ShowCategory(category.Id);
        navigator.Go(PageKey.Programs);
    }

    [RelayCommand]
    private void BrowseAll()
    {
        programs.ShowCategory(null);
        navigator.Go(PageKey.Programs);
    }

    [RelayCommand]
    private void SelectPreset(PresetViewModel preset)
    {
        var count = state.SelectMany(preset.Preset.SoftwareIds);
        Toast = count == 0
            ? Strings.Format("Toast.PresetAlreadySelected", preset.Name)
            : Strings.Format("Toast.PresetSelected", count, preset.Name);
    }

    [RelayCommand]
    private Task Redetect() => state.DetectSystemAsync();

    [RelayCommand]
    private Task InstallFlatpak() => state.InstallFlatpakAsync();

    public void RefreshRecent() => OnPropertyChanged(nameof(RecentText));
}

public sealed record StateFilterOption(InstallStateFilter Filter, string Name);

public sealed partial class ProgramsViewModel : ObservableObject
{
    private readonly LinuxCatalogState _state;

    public ProgramsViewModel(LinuxCatalogState state)
    {
        _state = state;
        CategoryChips = [new CategoryViewModel(null, Strings.Get("Programs.AllCategories"), "Icon.Programs", state.Items.Count), .. state.Categories];
        StateFilters =
        [
            new(InstallStateFilter.All, Strings.Get("Filter.All")),
            new(InstallStateFilter.Installed, Strings.Get("Filter.Installed")),
            new(InstallStateFilter.NotInstalled, Strings.Get("Filter.NotInstalled")),
            new(InstallStateFilter.Selected, Strings.Get("Filter.Selected")),
        ];
        _selectedCategory = CategoryChips[0];
        _selectedStateFilter = StateFilters[0];
        state.SelectionChanged += (_, _) => { if (SelectedStateFilter.Filter == InstallStateFilter.Selected) Apply(); };
        foreach (var item in state.Items)
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SoftwareItemViewModel.State) && SelectedStateFilter.Filter is InstallStateFilter.Installed or InstallStateFilter.NotInstalled) ScheduleApply(); };
        Apply();
    }

    public IReadOnlyList<CategoryViewModel> CategoryChips { get; }
    public IReadOnlyList<StateFilterOption> StateFilters { get; }
    public ObservableCollection<SoftwareItemViewModel> Results { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private CategoryViewModel _selectedCategory;

    [ObservableProperty]
    private StateFilterOption _selectedStateFilter;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    partial void OnSearchTextChanged(string value) => Apply();

    partial void OnSelectedCategoryChanged(CategoryViewModel value) => Apply();

    partial void OnSelectedStateFilterChanged(StateFilterOption value) => Apply();

    public void ShowSearch(string query)
    {
        SelectedCategory = CategoryChips[0];
        SearchText = query;
    }

    public void ShowCategory(string? categoryId)
    {
        SearchText = string.Empty;
        SelectedCategory = CategoryChips.FirstOrDefault(c => c.Id == categoryId) ?? CategoryChips[0];
    }

    private bool _applyScheduled;

    private void ScheduleApply()
    {
        if (_applyScheduled)
            return;
        _applyScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => { _applyScheduled = false; Apply(); }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Apply()
    {
        if (SelectedCategory is null || SelectedStateFilter is null)
            return;
        var filter = new CatalogFilter(SearchText, SelectedCategory.Id, SelectedStateFilter.Filter);
        var items = CatalogQuery.Apply(
            _state.Items,
            filter,
            i => i.Entry,
            i => i.State,
            i => i.IsSelected,
            id => _state.Catalog.FindCategory(id)?.Name.Get("tr") ?? id).ToList();

        Results.Clear();
        foreach (var item in items)
            Results.Add(item);
        IsEmpty = Results.Count == 0;
        ResultSummary = Results.Count == _state.Items.Count
            ? Strings.Format("Programs.TotalCount", _state.Items.Count)
            : Strings.Format("Programs.ResultCount", Results.Count, _state.Items.Count);
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var item in Results.Where(i => i.IsAvailable))
            item.IsSelected = true;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = string.Empty;
        SelectedCategory = CategoryChips[0];
        SelectedStateFilter = StateFilters[0];
    }

    [RelayCommand]
    private static void Toggle(SoftwareItemViewModel item)
    {
        if (item.IsAvailable)
            item.IsSelected = !item.IsSelected;
    }

    [RelayCommand]
    private static void OpenHomepage(SoftwareItemViewModel item)
    {
        if (item.Entry.Homepage is { } homepage)
            Shell.Open(homepage.AbsoluteUri);
    }
}

public sealed partial class HistoryViewModel(IHistoryStore history) : ObservableObject
{
    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private bool _confirmClear;

    public void Load()
    {
        Rows.Clear();
        var entries = history.Load();
        foreach (var entry in entries)
            Rows.Add(new HistoryRow(entry));
        IsEmpty = Rows.Count == 0;
        Summary = IsEmpty ? string.Empty : Strings.Format("History.Summary", Rows.Count, entries.Select(e => e.SessionId).Distinct().Count());
        ConfirmClear = false;
    }

    [RelayCommand]
    private void AskClear() => ConfirmClear = true;

    [RelayCommand]
    private void CancelClear() => ConfirmClear = false;

    [RelayCommand]
    private void Clear()
    {
        try
        {
            history.Clear();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = Strings.Get("History.ClearFailed");
            return;
        }
        Load();
    }
}

public sealed record HistoryRow(HistoryEntry Entry)
{
    public string Program => Entry.SoftwareName;
    public string Date => Entry.Timestamp.LocalDateTime.ToString("g", Strings.Localizer.Culture);
    public string StatusText => Strings.Get($"Status.{Entry.Status}");
    public string? Message => Entry.Message;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Entry.Message);
    public string MethodText => Entry.Method switch
    {
        InstallMethod.Flatpak => Strings.Get("Method.Flatpak"),
        InstallMethod.NativePackage => Strings.Get("Method.NativePackage"),
        InstallMethod.Winget => Strings.Get("Method.Winget"),
        InstallMethod.Direct => Strings.Get("Method.Direct"),
        _ => "–",
    };
    public PillKind Pill => Entry.Status switch
    {
        OutcomeStatus.Succeeded => PillKind.Success,
        OutcomeStatus.Failed => PillKind.Danger,
        _ => PillKind.Warning,
    };
}

public sealed record ThemeOption(ThemePreference Value, string Name);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _store;
    private readonly LinuxCatalogState _state;
    private readonly IUpdateChecker _updates;
    private readonly IAppPaths _paths;
    private bool _loading;

    public SettingsViewModel(ISettingsStore store, LinuxCatalogState state, IUpdateChecker updates, IAppPaths paths)
    {
        _store = store;
        _state = state;
        _updates = updates;
        _paths = paths;
        Themes =
        [
            new(ThemePreference.System, Strings.Get("Settings.Theme.System")),
            new(ThemePreference.Light, Strings.Get("Settings.Theme.Light")),
            new(ThemePreference.Dark, Strings.Get("Settings.Theme.Dark")),
        ];
        _loading = true;
        var settings = store.Load();
        _selectedTheme = Themes.First(t => t.Value == settings.Theme);
        _preferNative = settings.PreferNativePackages;
        _skipInstalled = settings.SkipInstalledByDefault;
        _loading = false;
        VersionText = Strings.Format("Settings.Version", AppInfo.VersionText);
    }

    public IReadOnlyList<ThemeOption> Themes { get; }

    public string VersionText { get; }

    public string DataDirectory => _paths.DataDirectory;

    public string SystemLine => _state.System is { } s
        ? Strings.Format("Linux.Settings.SystemLine", s.Distribution.PrettyName, s.Architecture, s.PackageManagerName, s.FlatpakVersion ?? "-")
        : Strings.Get("Linux.Status.Detecting");

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private bool _preferNative;

    [ObservableProperty]
    private bool _skipInstalled;

    [ObservableProperty]
    private string? _updateText;

    [ObservableProperty]
    private bool _updatePageAvailable;

    private Uri? _updatePage;

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        ApplyTheme(value.Value);
        Save();
    }

    partial void OnPreferNativeChanged(bool value) => Save();

    partial void OnSkipInstalledChanged(bool value) => Save();

    private void Save()
    {
        if (_loading)
            return;
        _store.Save(_store.Load() with
        {
            Theme = SelectedTheme.Value,
            PreferNativePackages = PreferNative,
            SkipInstalledByDefault = SkipInstalled,
        });
    }

    public static void ApplyTheme(ThemePreference theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    public void RefreshSystem() => OnPropertyChanged(nameof(SystemLine));

    [RelayCommand]
    private async Task CheckUpdates()
    {
        UpdateText = Strings.Get("Settings.Update.Checking");
        UpdatePageAvailable = false;
        var result = await _updates.CheckAsync(AppInfo.Version, CancellationToken.None);
        _updatePage = result.ReleaseUrl;
        UpdatePageAvailable = result.ReleaseUrl is not null;
        UpdateText = result switch
        {
            { Succeeded: false } => Strings.Get("Settings.Update.Failed"),
            { Message: "noReleases" } => Strings.Get("Settings.Update.NoReleases"),
            { UpdateAvailable: true } => Strings.Format("Settings.Update.Available", result.LatestVersion),
            _ => Strings.Format("Settings.Update.UpToDate", AppInfo.VersionText),
        };
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (_updatePage is not null)
            Shell.Open(_updatePage.AbsoluteUri);
    }

    [RelayCommand]
    private void OpenData() => Shell.Open(_paths.DataDirectory);

    [RelayCommand]
    private void OpenLogs() => Shell.Open(_paths.LogDirectory);

    [RelayCommand]
    private static void OpenUrl(string url) => Shell.Open(url);
}
