using System.ComponentModel;
using System.Windows;
using DeployNest.App.Localization;
using DeployNest.App.Services;
using DeployNest.App.ViewModels;

namespace DeployNest.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ThemeService _theme;

    public MainWindow(MainViewModel viewModel, ThemeService theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _theme = theme;
        DataContext = viewModel;
        SourceInitialized += (_, _) => _theme.ApplyTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var session = _viewModel.Session;
        if (session.IsRunning && !session.CloseWhenFinished)
        {
            e.Cancel = true;
            _ = AskToCloseAsync();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>Closing during an installation: pending items are canceled, the running installer is allowed to finish.</summary>
    private async Task AskToCloseAsync()
    {
        var loc = LocalizationSource.Instance;
        var answer = await _viewModel.Dialogs.ShowAsync(new DialogRequest
        {
            Title = loc["Close.Title"],
            Message = loc["Close.Message"],
            PrimaryText = loc["Close.StopAndClose"],
            CloseText = loc["Close.KeepRunning"],
            PrimaryIsDanger = true,
        });
        if (answer != Services.DialogResult.Primary)
            return;

        _viewModel.Session.CloseWhenFinished = true;
        _viewModel.Session.RequestStop();
        _viewModel.Toasts.Show(loc["Close.WillClose"]);
        _viewModel.Navigation.Navigate(PageKey.Progress);
    }
}
