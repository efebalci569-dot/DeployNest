using Avalonia.Controls;
using DeployNest.Linux.ViewModels;

namespace DeployNest.Linux.Views;

public partial class MainWindow : Window
{
    private bool _closeWhenFinished;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Closing during an installation cancels the pending items, lets the running one finish
    /// and closes the window afterwards (a package manager must not be killed half-way).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel { Controller.IsRunning: true } vm && !_closeWhenFinished)
        {
            e.Cancel = true;
            _closeWhenFinished = true;
            vm.Controller.SessionCompleted += (_, _) => Close();
            vm.Controller.RequestStop();
            vm.Navigate(PageKey.Install);
        }
        base.OnClosing(e);
    }
}
