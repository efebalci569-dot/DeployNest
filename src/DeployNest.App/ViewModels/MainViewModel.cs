using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Services;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;

namespace DeployNest.App.ViewModels;

/// <summary>Shell: sidebar navigation, selection bar, theme toggle, dialogs and toasts.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly ILocalizer _loc;

    public MainViewModel(
        NavigationService navigation,
        CatalogState state,
        InstallSessionController session,
        DialogService dialogs,
        ToastService toasts,
        ThemeService theme,
        ISettingsStore settings,
        ILocalizer localizer)
    {
        Navigation = navigation;
        State = state;
        Session = session;
        Dialogs = dialogs;
        Toasts = toasts;
        Theme = theme;
        _settings = settings;
        _loc = localizer;

        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationService.CurrentPage))
            {
                OnPropertyChanged(nameof(CurrentSection));
                OnPropertyChanged(nameof(ShowSelectionBar));
            }
        };
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CatalogState.SelectedCount))
                OnPropertyChanged(nameof(ShowSelectionBar));
        };
        session.PropertyChanged += OnSessionChanged;
        theme.ThemeChanged += (_, _) => OnPropertyChanged(nameof(ThemeGlyph));
    }

    public NavigationService Navigation { get; }
    public CatalogState State { get; }
    public InstallSessionController Session { get; }
    public DialogService Dialogs { get; }
    public ToastService Toasts { get; }
    public ThemeService Theme { get; }

    public string CurrentSection => Navigation.CurrentKey switch
    {
        PageKey.Programs or PageKey.Details => "Programs",
        PageKey.Queue or PageKey.Progress or PageKey.Results => "Install",
        var key => key.ToString(),
    };

    public bool ShowSelectionBar =>
        State.SelectedCount > 0 && Navigation.CurrentKey is PageKey.Home or PageKey.Programs or PageKey.Details;

    public string InstallBadge => Session.IsRunning ? $"{Session.CompletedCount}/{Session.TotalCount}" : string.Empty;

    public string ThemeGlyph => Theme.IsDark ? "" : "";

    public string ThemeTooltip => _loc["Nav.ToggleTheme"];

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallSessionController.CompletedCount) or nameof(InstallSessionController.TotalCount)
            or nameof(InstallSessionController.Stage))
        {
            OnPropertyChanged(nameof(InstallBadge));
        }
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        switch (section)
        {
            case "Install":
                Navigation.Navigate(Session.Stage switch
                {
                    SessionStage.Running => PageKey.Progress,
                    SessionStage.Completed when State.SelectedCount == 0 => PageKey.Results,
                    _ => PageKey.Queue,
                });
                break;
            default:
                Navigation.Navigate(Enum.Parse<PageKey>(section));
                break;
        }
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = Theme.IsDark ? ThemePreference.Light : ThemePreference.Dark;
        Theme.Apply(next);
        _settings.Save(_settings.Load() with { Theme = next });
    }

    [RelayCommand]
    private void ViewSelection() => Navigation.Navigate(PageKey.Selection);

    [RelayCommand]
    private void StartInstall() => Navigation.Navigate(Session.IsRunning ? PageKey.Progress : PageKey.Queue);

    [RelayCommand]
    private void ClearSelection()
    {
        var count = State.SelectedCount;
        State.ClearSelection();
        Toasts.Show(_loc.Format("Toast.SelectionCleared", count));
    }
}
