using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.App.ViewModels;
using DeployNest.Core.Abstractions;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.Services;

public enum SessionStage
{
    Idle,
    Running,
    Completed,
}

/// <summary>
/// Owns the lifetime of one installation session: runs the engine off the UI thread, turns
/// engine events into item view models, writes history and the report when it ends.
/// </summary>
public sealed partial class InstallSessionController : ObservableObject
{
    private readonly InstallSessionRunner _runner;
    private readonly CatalogState _state;
    private readonly IHistoryStore _history;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly ILocalizer _loc;
    private readonly NavigationService _navigation;
    private readonly ILogger<InstallSessionController> _logger;
    private SessionCancellation? _cancellation;

    public InstallSessionController(
        InstallSessionRunner runner,
        CatalogState state,
        IHistoryStore history,
        ISettingsStore settings,
        IAppPaths paths,
        ILocalizer localizer,
        NavigationService navigation,
        ILogger<InstallSessionController> logger)
    {
        _runner = runner;
        _state = state;
        _history = history;
        _settings = settings;
        _paths = paths;
        _loc = localizer;
        _navigation = navigation;
        _logger = logger;
    }

    public ObservableCollection<ProgressItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private SessionStage _stage = SessionStage.Idle;

    [ObservableProperty]
    private SessionResult? _lastResult;

    [ObservableProperty]
    private string? _lastReportPath;

    [ObservableProperty]
    private string? _lastReportText;

    [ObservableProperty]
    private DateTimeOffset _startedAt;

    [ObservableProperty]
    private int _completedCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private ProgressItemViewModel? _current;

    [ObservableProperty]
    private bool _stopRequested;

    [ObservableProperty]
    private bool _abortRequested;

    public bool IsRunning => Stage == SessionStage.Running;

    /// <summary>Set when the user asked to close the window; it closes after the session ends.</summary>
    public bool CloseWhenFinished { get; set; }

    public event EventHandler? SessionCompleted;

    public async Task StartAsync(InstallPlan plan)
    {
        if (IsRunning || _runner.IsRunning)
            throw new InvalidOperationException(_loc["Session.AlreadyRunning"]);

        var settings = _settings.Load();
        var options = new InstallRunOptions(
            settings.MaxParallelDownloads,
            settings.DownloadDirectory ?? _paths.DefaultDownloadDirectory,
            settings.KeepDownloadedFiles);

        Items.Clear();
        foreach (var item in plan.Items)
        {
            var software = _state.Find(item.Software.Id)!;
            Items.Add(new ProgressItemViewModel(software, item, _loc));
        }

        TotalCount = Items.Count;
        CompletedCount = 0;
        StopRequested = false;
        AbortRequested = false;
        LastResult = null;
        LastReportPath = null;
        LastReportText = null;
        StartedAt = DateTimeOffset.Now;
        Current = null;
        Stage = SessionStage.Running;

        _cancellation = new SessionCancellation();
        var events = new Progress<SessionEvent>(OnEvent);
        SessionResult? result = null;
        try
        {
            result = await Task.Run(() => _runner.RunAsync(plan, options, events, _cancellation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kurulum oturumu beklenmedik şekilde sonlandı");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }

        // Progress<T> posts asynchronously; let queued item events reach the UI before finishing.
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Finish(result);
    }

    public void RequestStop()
    {
        if (!IsRunning || _cancellation is null)
            return;
        StopRequested = true;
        _cancellation.RequestStop();
        foreach (var item in Items.Where(i => !i.IsDone && !i.IsActive))
            item.MarkCanceling();
    }

    public void RequestAbort()
    {
        if (!IsRunning || _cancellation is null)
            return;
        StopRequested = true;
        AbortRequested = true;
        _cancellation.RequestAbort();
    }

    public void Reset()
    {
        if (IsRunning)
            return;
        Stage = SessionStage.Idle;
    }

    private void OnEvent(SessionEvent e)
    {
        var item = Items.FirstOrDefault(i => i.Id == e.SoftwareId);
        if (item is null)
            return;

        switch (e)
        {
            case ItemProgressEvent progress:
                item.Apply(progress.Progress);
                if (progress.Progress.Phase is ItemPhase.Preparing or ItemPhase.Installing)
                    Current = item;
                break;
            case ItemCompletedEvent completed:
                item.Complete(completed.Result);
                CompletedCount = Items.Count(i => i.IsDone);
                break;
        }
    }

    private void Finish(SessionResult? result)
    {
        Current = null;
        LastResult = result;
        Stage = SessionStage.Completed;

        if (result is not null)
        {
            foreach (var item in result.Items)
                Items.FirstOrDefault(i => i.Id == item.Software.Id)?.Complete(item);
            CompletedCount = Items.Count(i => i.IsDone);
            SaveHistory(result);
            SaveReport(result);
        }

        _ = _state.RefreshDetectionAsync();
        SessionCompleted?.Invoke(this, EventArgs.Empty);

        if (CloseWhenFinished)
        {
            Application.Current.MainWindow?.Close();
            return;
        }

        if (_navigation.CurrentKey is PageKey.Progress or PageKey.Queue)
            _navigation.Navigate(PageKey.Results);
    }

    private void SaveHistory(SessionResult result)
    {
        var describer = new FailureDescriber(_loc);
        var entries = result.Items.Select(i => new HistoryEntry(
            result.SessionId,
            i.StartedAt,
            i.Software.Id,
            i.Software.Name,
            "install",
            i.Method,
            i.Outcome.Status,
            i.Outcome.Failure,
            i.Outcome.Status == OutcomeStatus.Succeeded ? null : describer.Summarize(i.Outcome),
            i.Duration));
        try
        {
            _history.Append(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Kurulum geçmişi kaydedilemedi");
        }
    }

    private void SaveReport(SessionResult result)
    {
        var text = new ReportBuilder(_loc).Build(result, AppInfo.Version, _paths.LogDirectory);
        LastReportText = text;
        try
        {
            Directory.CreateDirectory(_paths.ReportDirectory);
            var path = Path.Combine(_paths.ReportDirectory, $"DeployNest-Rapor-{result.StartedAt.LocalDateTime:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            LastReportPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Rapor kaydedilemedi");
        }
    }
}
