using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.Linux.Services;

namespace DeployNest.Linux.ViewModels;

public enum PageKey
{
    Home,
    Programs,
    Install,
    History,
    Settings,
}

public sealed record NavItem(PageKey Key, string Title, string IconKey);

/// <summary>Window shell: sidebar navigation, current page and the selection footer.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly LinuxCatalogState _state;
    private readonly LinuxInstallController _controller;
    private readonly Func<PageKey, ObservableObject> _pages;
    private bool _syncingNav;

    public MainWindowViewModel(LinuxCatalogState state, LinuxInstallController controller, Func<PageKey, ObservableObject> pages)
    {
        _state = state;
        _controller = controller;
        _pages = pages;
        NavItems =
        [
            new(PageKey.Home, Strings.Get("Nav.Home"), "Icon.Home"),
            new(PageKey.Programs, Strings.Get("Nav.Programs"), "Icon.Programs"),
            new(PageKey.Install, Strings.Get("Linux.Nav.Install"), "Icon.Install"),
            new(PageKey.History, Strings.Get("Nav.History"), "Icon.History"),
            new(PageKey.Settings, Strings.Get("Nav.Settings"), "Icon.Settings"),
        ];
        _selectedNav = NavItems[0];
        _currentPage = pages(PageKey.Home);
        state.SelectionChanged += (_, _) => OnPropertyChanged(nameof(CanReview));
        controller.SessionCompleted += (_, _) =>
        {
            if (SelectedNav.Key == PageKey.Install)
                Navigate(PageKey.Install);
        };
    }

    public LinuxCatalogState State => _state;

    public LinuxInstallController Controller => _controller;

    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem _selectedNav;

    [ObservableProperty]
    private ObservableObject _currentPage;

    public bool CanReview => _state.SelectedCount > 0 || _controller.IsRunning;

    partial void OnSelectedNavChanged(NavItem value)
    {
        if (!_syncingNav && value is not null)
            Navigate(value.Key);
    }

    [RelayCommand]
    public void Navigate(PageKey key)
    {
        var page = _pages(key);
        if (page is QueueViewModel queue)
            queue.Refresh();
        if (page is HistoryViewModel historyPage)
            historyPage.Load();
        CurrentPage = page;

        _syncingNav = true;
        SelectedNav = NavItems.First(n => n.Key == key);
        _syncingNav = false;
    }

    [RelayCommand]
    private void Review() => Navigate(PageKey.Install);
}
