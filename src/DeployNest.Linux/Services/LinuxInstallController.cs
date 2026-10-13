using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.Core.Abstractions;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Infrastructure.Linux;
using DeployNest.Linux.ViewModels;
using Microsoft.Extensions.Logging;

namespace DeployNest.Linux.Services;

public enum SessionStage
{
    Idle,
    Running,
    Completed,
}

/// <summary>Runs an installation plan on Linux and keeps its live state for the progress and results pages.</summary>
public sealed partial class LinuxInstallController(
    LinuxCatalogState state,
    FlatpakInstallStrategy flatpakStrategy,
    NativePackageStrategy nativeStrategy,
    PrivilegedSessionProvider elevation,
    IClock clock,
    IHistoryStore history,
    IAppPaths paths,
    ILoggerFactory loggerFactory,
    ILogger<LinuxInstallController> logger) : ObservableObject
{
    private SessionCancellation? _cancellation;

    public ObservableCollection<ProgressItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsCompleted))]
    private SessionStage _stage = SessionStage.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(OverallPercent))]
    private int _completedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(OverallPercent))]
    private int _totalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _stopRequested;

    [ObservableProperty]
    private SessionResult? _lastResult;

    [ObservableProperty]
    private string? _lastReportPath;

    public bool IsRunning => Stage == SessionStage.Running;

    public bool IsCompleted => Stage == SessionStage.Completed;

    public double OverallPercent => TotalCount == 0 ? 0 : 100.0 * CompletedCount / TotalCount;

    public string StatusText => StopRequested && IsRunning
        ? Strings.Get("Progress.Stopping")
        : IsCompleted ? Strings.Get("Progress.Done") : Strings.Format("Progress.Status", CompletedCount, TotalCount);

    public event EventHandler? SessionCompleted;

    public async Task StartAsync(InstallPlan plan)
    {
        if (IsRunning)
            throw new InvalidOperationException(Strings.Get("Session.AlreadyRunning"));

        Items.Clear();
        var manager = state.System?.PackageManager;
        foreach (var item in plan.Items)
            Items.Add(new ProgressItemViewModel(state.Find(item.Software.Id)!, item, manager));

        TotalCount = Items.Count;
        CompletedCount = 0;
        StopRequested = false;
        LastResult = null;
        LastReportPath = null;
        Stage = SessionStage.Running;

        var runner = new InstallSessionRunner(
            [flatpakStrategy, nativeStrategy],
            clock,
            loggerFactory.CreateLogger<InstallSessionRunner>(),
            state.IsInstalledAsync);
        var options = new InstallRunOptions(1, paths.DefaultDownloadDirectory, false);

        _cancellation = new SessionCancellation();
        var events = new Progress<SessionEvent>(OnEvent);
        SessionResult? result = null;
        try
        {
            result = await Task.Run(() => runner.RunAsync(plan, options, events, _cancellation));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Kurulum oturumu beklenmedik şekilde sonlandı");
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            // Closes the root helper; the next session asks for the password again.
            await elevation.ResetAsync();
        }

        // Progress<T> posts asynchronously; let queued item events arrive before finishing.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Finish(result);
    }

    public void RequestStop()
    {
        if (!IsRunning || _cancellation is null)
            return;
        StopRequested = true;
        _cancellation.RequestStop();
        foreach (var item in Items)
            item.MarkCanceling();
    }

    public void RequestAbort()
    {
        if (!IsRunning || _cancellation is null)
            return;
        StopRequested = true;
        _cancellation.RequestAbort();
    }

    public void Reset()
    {
        if (!IsRunning)
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
                break;
            case ItemCompletedEvent completed:
                item.Complete(completed.Result);
                CompletedCount = Items.Count(i => i.IsDone);
                break;
        }
    }

    private void Finish(SessionResult? result)
    {
        LastResult = result;
        if (result is not null)
        {
            foreach (var item in result.Items)
                Items.FirstOrDefault(i => i.Id == item.Software.Id)?.Complete(item);
            CompletedCount = Items.Count(i => i.IsDone);
            SaveHistory(result);
            SaveReport(result);
        }
        Stage = SessionStage.Completed;
        _ = state.RefreshDetectionAsync();
        SessionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SaveHistory(SessionResult result)
    {
        var describer = new FailureDescriber(Strings.Localizer, "Linux");
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
            history.Append(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Kurulum geçmişi kaydedilemedi");
        }
    }

    private void SaveReport(SessionResult result)
    {
        var text = new ReportBuilder(Strings.Localizer, "Linux").Build(result, AppInfo.VersionText, paths.LogDirectory);
        try
        {
            Directory.CreateDirectory(paths.ReportDirectory);
            var path = Path.Combine(paths.ReportDirectory, $"DeployNest-Rapor-{result.StartedAt.LocalDateTime:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            LastReportPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Rapor kaydedilemedi");
        }
    }
}
