using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Controls;
using DeployNest.App.Services;
using DeployNest.Core.Catalog;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;

namespace DeployNest.App.ViewModels;

public sealed record PresetViewModel(string Id, string Name, string Description, string Glyph, IReadOnlyList<string> SoftwareIds, string Preview)
{
    public int Count => SoftwareIds.Count;
}

public sealed record RecentEntryViewModel(string Name, string DateText, string ResultText, PillKind Kind, string Glyph);

public sealed record ProgramsQuery(string? Search = null, string? CategoryId = null, InstallStateFilter? State = null);

public sealed partial class HomeViewModel : ObservableObject, IPageViewModel
{
    private readonly NavigationService _navigation;
    private readonly ToastService _toasts;
    private readonly IHistoryStore _history;
    private readonly ShellService _shell;
    private readonly ILocalizer _loc;

    public HomeViewModel(CatalogState state, NavigationService navigation, ToastService toasts, IHistoryStore history, ShellService shell, ILocalizer localizer)
    {
        State = state;
        _navigation = navigation;
        _toasts = toasts;
        _history = history;
        _shell = shell;
        _loc = localizer;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogState.WingetStatus))
                OnPropertyChanged(nameof(ShowWingetWarning));
        };
    }

    public PageKey Key => PageKey.Home;

    public CatalogState State { get; }

    public IReadOnlyList<PresetViewModel> Presets => State.Catalog.Presets
        .Select(p => new PresetViewModel(
            p.Id,
            p.Name.Get(_loc.Language),
            p.Description.Get(_loc.Language),
            p.IconGlyph,
            p.SoftwareIds,
            string.Join(" · ", p.SoftwareIds.Select(id => State.Find(id)?.Name).Where(n => n is not null))))
        .ToList();

    public IReadOnlyList<CategoryViewModel> Categories => State.Categories;

    public ObservableCollection<RecentEntryViewModel> Recent { get; } = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string? _recentSummary;

    public bool ShowWingetWarning => State.WingetStatus is { Available: false };

    public string? CatalogWarning => State.CatalogIssues.Count > 0 || State.CatalogUsedFallback
        ? _loc.Format("Home.CatalogIssues", State.CatalogIssues.Count)
        : null;

    public void OnNavigatedTo(object? parameter)
    {
        SearchText = string.Empty;
        LoadRecent();
    }

    private void LoadRecent()
    {
        Recent.Clear();
        var entries = _history.Load();
        if (entries.Count == 0)
        {
            RecentSummary = null;
            return;
        }

        var lastSession = entries[0].SessionId;
        var session = entries.Where(e => e.SessionId == lastSession).ToList();
        RecentSummary = _loc.Format("Home.Recent.Summary",
            session.Max(e => e.Timestamp).LocalDateTime.ToString("g", _loc.Culture),
            session.Count(e => e.Status == OutcomeStatus.Succeeded),
            session.Count(e => e.Status == OutcomeStatus.Failed),
            session.Count(e => e.Status is OutcomeStatus.Skipped or OutcomeStatus.Canceled));

        foreach (var entry in entries.Take(5))
        {
            var (kind, glyph) = entry.Status switch
            {
                OutcomeStatus.Succeeded => (PillKind.Success, ""),
                OutcomeStatus.Failed => (PillKind.Danger, ""),
                _ => (PillKind.Warning, ""),
            };
            Recent.Add(new RecentEntryViewModel(
                entry.SoftwareName,
                entry.Timestamp.LocalDateTime.ToString("g", _loc.Culture),
                _loc[$"Status.{entry.Status}"],
                kind,
                glyph));
        }
    }

    [RelayCommand]
    private void Search() => _navigation.Navigate(PageKey.Programs, new ProgramsQuery(Search: SearchText));

    [RelayCommand]
    private void BrowseAll() => _navigation.Navigate(PageKey.Programs, new ProgramsQuery());

    [RelayCommand]
    private void SelectPreset(PresetViewModel preset)
    {
        var before = State.SelectedCount;
        State.SelectMany(preset.SoftwareIds);
        var added = State.SelectedCount - before;
        _toasts.Show(added > 0
            ? _loc.Format("Toast.PresetSelected", added, preset.Name)
            : _loc.Format("Toast.PresetAlreadySelected", preset.Name));
    }

    [RelayCommand]
    private void OpenCategory(CategoryViewModel category) =>
        _navigation.Navigate(PageKey.Programs, new ProgramsQuery(CategoryId: category.Id));

    [RelayCommand]
    private void OpenHistory() => _navigation.Navigate(PageKey.History);

    [RelayCommand]
    private void GetWinget() => _shell.OpenUrl(AppInfo.AppInstallerStoreUrl);

    [RelayCommand]
    private void OpenWingetDocs() => _shell.OpenUrl(AppInfo.WingetDocsUrl);

    [RelayCommand]
    private Task RefreshDetection() => State.RefreshDetectionAsync();
}
