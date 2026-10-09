using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DeployNest.App.Services;

public enum PageKey
{
    Home,
    Programs,
    Details,
    Selection,
    Queue,
    Progress,
    Results,
    History,
    Settings,
    About,
}

public interface IPageViewModel
{
    PageKey Key { get; }

    /// <summary>Called every time the page is shown. Long work must not block the UI thread.</summary>
    void OnNavigatedTo(object? parameter);
}

/// <summary>Page navigation with a back stack. Page view models are singletons so their state is kept.</summary>
public sealed partial class NavigationService(IServiceProvider services) : ObservableObject
{
    private readonly Stack<(PageKey Key, object? Parameter)> _history = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentKey))]
    private IPageViewModel? _currentPage;

    private object? _currentParameter;

    public PageKey CurrentKey => CurrentPage?.Key ?? PageKey.Home;

    public bool CanGoBack => _history.Count > 0;

    public event EventHandler? Navigated;

    public void Navigate(PageKey key, object? parameter = null)
    {
        if (CurrentPage is not null)
        {
            if (CurrentPage.Key == key && Equals(parameter, _currentParameter) && key != PageKey.Queue)
                return;
            _history.Push((CurrentPage.Key, _currentParameter));
            if (_history.Count > 30)
                TrimHistory();
        }

        Show(key, parameter);
    }

    public void GoBack()
    {
        if (_history.Count == 0)
        {
            Show(PageKey.Home, null);
            return;
        }
        var (key, parameter) = _history.Pop();
        Show(key, parameter);
    }

    private void Show(PageKey key, object? parameter)
    {
        var page = Resolve(key);
        _currentParameter = parameter;
        page.OnNavigatedTo(parameter);
        CurrentPage = page;
        OnPropertyChanged(nameof(CanGoBack));
        Navigated?.Invoke(this, EventArgs.Empty);
    }

    private IPageViewModel Resolve(PageKey key) => key switch
    {
        PageKey.Home => services.GetRequiredService<HomeViewModel>(),
        PageKey.Programs => services.GetRequiredService<ProgramsViewModel>(),
        PageKey.Details => services.GetRequiredService<ProgramDetailsViewModel>(),
        PageKey.Selection => services.GetRequiredService<SelectionViewModel>(),
        PageKey.Queue => services.GetRequiredService<QueueViewModel>(),
        PageKey.Progress => services.GetRequiredService<ProgressViewModel>(),
        PageKey.Results => services.GetRequiredService<ResultsViewModel>(),
        PageKey.History => services.GetRequiredService<HistoryViewModel>(),
        PageKey.Settings => services.GetRequiredService<SettingsViewModel>(),
        PageKey.About => services.GetRequiredService<AboutViewModel>(),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    private void TrimHistory()
    {
        var keep = _history.Take(20).Reverse().ToList();
        _history.Clear();
        foreach (var entry in keep)
            _history.Push(entry);
    }
}
