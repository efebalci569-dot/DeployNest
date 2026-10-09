using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Services;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.ViewModels;

public sealed partial class ProgressViewModel : ObservableObject, IPageViewModel
{
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly ILocalizer _loc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ProgressViewModel(InstallSessionController session, NavigationService navigation, DialogService dialogs, ILocalizer localizer)
    {
        Session = session;
        _navigation = navigation;
        _dialogs = dialogs;
        _loc = localizer;
        _timer.Tick += (_, _) => UpdateTexts();
        session.PropertyChanged += OnSessionChanged;
    }

    public PageKey Key => PageKey.Progress;

    public InstallSessionController Session { get; }

    [ObservableProperty]
    private string _elapsedText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _overallPercent;

    [ObservableProperty]
    private string? _currentText;

    public void OnNavigatedTo(object? parameter)
    {
        UpdateTexts();
        if (Session.IsRunning)
            _timer.Start();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InstallSessionController.Stage))
        {
            if (Session.IsRunning) _timer.Start();
            else _timer.Stop();
        }
        UpdateTexts();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void UpdateTexts()
    {
        if (Session.Stage == SessionStage.Idle)
        {
            StatusText = _loc["Progress.NoSession"];
            ElapsedText = string.Empty;
            OverallPercent = 0;
            CurrentText = null;
            return;
        }

        var elapsed = (Session.LastResult?.FinishedAt ?? DateTimeOffset.Now) - Session.StartedAt;
        ElapsedText = _loc.Format("Progress.Elapsed", new ReportBuilder(_loc).FormatDuration(elapsed));
        OverallPercent = Session.TotalCount == 0 ? 0 : Session.CompletedCount * 100.0 / Session.TotalCount;
        StatusText = Session.Stage == SessionStage.Completed
            ? _loc["Progress.Done"]
            : Session.AbortRequested ? _loc["Progress.Aborting"]
            : Session.StopRequested ? _loc["Progress.Stopping"]
            : _loc.Format("Progress.Status", Session.CompletedCount, Session.TotalCount);
        CurrentText = Session.Current is { } current && Session.IsRunning
            ? _loc.Format("Progress.Current", current.Software.Name, current.PhaseText)
            : null;
    }

    private bool CanCancel() => Session.IsRunning && !Session.AbortRequested;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task Cancel()
    {
        if (!Session.StopRequested)
        {
            var answer = await _dialogs.ShowAsync(new DialogRequest
            {
                Title = _loc["Cancel.Title"],
                Message = _loc["Cancel.Message"],
                PrimaryText = _loc["Cancel.Stop"],
                SecondaryText = _loc["Cancel.Abort"],
                CloseText = _loc["Cancel.Continue"],
            });
            if (answer == DialogResult.Primary)
                Session.RequestStop();
            else if (answer == DialogResult.Secondary)
                await ConfirmAbortAsync();
        }
        else
        {
            await ConfirmAbortAsync();
        }
        UpdateTexts();
    }

    private async Task ConfirmAbortAsync()
    {
        var answer = await _dialogs.ShowAsync(new DialogRequest
        {
            Title = _loc["Abort.Title"],
            Message = _loc["Abort.Message"],
            PrimaryText = _loc["Abort.Confirm"],
            CloseText = _loc["Cancel.Continue"],
            PrimaryIsDanger = true,
        });
        if (answer == DialogResult.Primary)
            Session.RequestAbort();
    }

    [RelayCommand]
    private void ViewResults() => _navigation.Navigate(PageKey.Results);
}

public sealed partial class ResultsViewModel : ObservableObject, IPageViewModel
{
    private readonly CatalogState _state;
    private readonly NavigationService _navigation;
    private readonly FileDialogService _files;
    private readonly ShellService _shell;
    private readonly ToastService _toasts;
    private readonly IAppPaths _paths;
    private readonly ILocalizer _loc;
    private readonly ILogger<ResultsViewModel> _logger;

    public ResultsViewModel(
        InstallSessionController session,
        CatalogState state,
        NavigationService navigation,
        FileDialogService files,
        ShellService shell,
        ToastService toasts,
        IAppPaths paths,
        ILocalizer localizer,
        ILogger<ResultsViewModel> logger)
    {
        Session = session;
        _state = state;
        _navigation = navigation;
        _files = files;
        _shell = shell;
        _toasts = toasts;
        _paths = paths;
        _loc = localizer;
        _logger = logger;
    }

    public PageKey Key => PageKey.Results;

    public InstallSessionController Session { get; }

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _headline = string.Empty;

    [ObservableProperty]
    private string _subline = string.Empty;

    [ObservableProperty]
    private int _succeeded;

    [ObservableProperty]
    private int _failed;

    [ObservableProperty]
    private int _skipped;

    [ObservableProperty]
    private int _canceled;

    [ObservableProperty]
    private bool _rebootRequired;

    public void OnNavigatedTo(object? parameter)
    {
        var result = Session.LastResult;
        HasResult = result is not null;
        if (result is null)
        {
            Headline = _loc["Results.None"];
            Subline = _loc["Results.None.Hint"];
            return;
        }

        Succeeded = result.Succeeded.Count();
        Failed = result.Failed.Count();
        Skipped = result.Skipped.Count();
        Canceled = result.Canceled.Count();
        RebootRequired = result.RebootRequired;
        Headline = result.CancelRequested ? _loc["Results.Headline.Canceled"]
            : Failed == 0 ? _loc["Results.Headline.Success"]
            : _loc.Format("Results.Headline.Partial", Failed);
        Subline = _loc.Format("Results.Subline",
            result.StartedAt.LocalDateTime.ToString("g", _loc.Culture),
            new ReportBuilder(_loc).FormatDuration(result.Duration));
        RetryFailedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ExportReport()
    {
        if (Session.LastReportText is not { } text || Session.LastResult is not { } result)
            return;

        var path = _files.SaveTextFile($"DeployNest-Rapor-{result.StartedAt.LocalDateTime:yyyyMMdd-HHmm}.txt", _loc["Results.Export.Title"]);
        if (path is null)
            return;
        try
        {
            File.WriteAllText(path, text, new UTF8Encoding(true));
            _toasts.Show(_loc.Format("Results.Export.Done", Path.GetFileName(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rapor dışa aktarılamadı");
            _toasts.Show(_loc["Results.Export.Failed"]);
        }
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (Session.LastReportPath is { } path)
            _shell.OpenFile(path);
    }

    [RelayCommand]
    private void OpenLogs() => _shell.OpenFolder(_paths.LogDirectory);

    private bool CanRetry() => Session.LastResult?.Failed.Any() == true && !Session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void RetryFailed()
    {
        if (Session.LastResult is not { } result)
            return;
        _state.ClearSelection();
        _state.SelectMany(result.Failed.Select(f => f.Software.Id));
        _navigation.Navigate(PageKey.Queue);
    }

    [RelayCommand]
    private void Done()
    {
        Session.Reset();
        _state.ClearSelection();
        _navigation.Navigate(PageKey.Home);
    }
}
