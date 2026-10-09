using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Services;
using DeployNest.Core.Catalog;
using DeployNest.Core.Localization;

namespace DeployNest.App.ViewModels;

public sealed record StateFilterOption(InstallStateFilter Value, string Name);

public sealed partial class ProgramsViewModel : ObservableObject, IPageViewModel
{
    private readonly NavigationService _navigation;
    private readonly ToastService _toasts;
    private readonly ILocalizer _loc;
    private bool _suspendRefresh;

    public ProgramsViewModel(CatalogState state, NavigationService navigation, ToastService toasts, ILocalizer localizer)
    {
        State = state;
        _navigation = navigation;
        _toasts = toasts;
        _loc = localizer;

        CategoryChips = [new CategoryViewModel(null, localizer["Programs.AllCategories"], "", state.Items.Count), .. state.Categories];
        StateFilters =
        [
            new(InstallStateFilter.All, localizer["Filter.All"]),
            new(InstallStateFilter.Installed, localizer["Filter.Installed"]),
            new(InstallStateFilter.NotInstalled, localizer["Filter.NotInstalled"]),
            new(InstallStateFilter.Selected, localizer["Filter.Selected"]),
        ];
        _selectedCategory = CategoryChips[0];
        _selectedStateFilter = StateFilters[0];

        state.DetectionUpdated += (_, _) =>
        {
            if (SelectedStateFilter.Value is InstallStateFilter.Installed or InstallStateFilter.NotInstalled)
                Refresh();
        };
        state.SelectionChanged += (_, _) =>
        {
            if (SelectedStateFilter.Value == InstallStateFilter.Selected)
                Refresh();
        };
        Refresh();
    }

    public PageKey Key => PageKey.Programs;

    public CatalogState State { get; }

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

    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(SearchText) || SelectedCategory.Id is not null || SelectedStateFilter.Value != InstallStateFilter.All;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCategoryChanged(CategoryViewModel value) => Refresh();

    partial void OnSelectedStateFilterChanged(StateFilterOption value) => Refresh();

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is not ProgramsQuery query)
            return;

        _suspendRefresh = true;
        SearchText = query.Search ?? string.Empty;
        SelectedCategory = CategoryChips.FirstOrDefault(c => c.Id == query.CategoryId) ?? CategoryChips[0];
        SelectedStateFilter = StateFilters.First(f => f.Value == (query.State ?? InstallStateFilter.All));
        _suspendRefresh = false;
        Refresh();
    }

    private void Refresh()
    {
        if (_suspendRefresh)
            return;

        var filter = new CatalogFilter(SearchText, SelectedCategory?.Id, SelectedStateFilter?.Value ?? InstallStateFilter.All);
        var results = CatalogQuery.Apply(
            State.Items,
            filter,
            i => i.Entry,
            i => i.State,
            i => i.IsSelected,
            id => State.Catalog.FindCategory(id)?.Name.Get(_loc.Language) ?? string.Empty);

        if (!results.SequenceEqual(Results))
        {
            Results.Clear();
            foreach (var item in results)
                Results.Add(item);
        }

        IsEmpty = Results.Count == 0;
        ResultSummary = HasActiveFilters
            ? _loc.Format("Programs.ResultCount", Results.Count, State.Items.Count)
            : _loc.Format("Programs.TotalCount", State.Items.Count);
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    [RelayCommand]
    private void OpenDetails(SoftwareItemViewModel item) => _navigation.Navigate(PageKey.Details, item);

    [RelayCommand]
    private void ClearFilters()
    {
        _suspendRefresh = true;
        SearchText = string.Empty;
        SelectedCategory = CategoryChips[0];
        SelectedStateFilter = StateFilters[0];
        _suspendRefresh = false;
        Refresh();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        var count = 0;
        foreach (var item in Results.Where(i => !i.IsSelected))
        {
            item.IsSelected = true;
            count++;
        }
        if (count > 0)
            _toasts.Show(_loc.Format("Toast.Selected", count));
    }
}
