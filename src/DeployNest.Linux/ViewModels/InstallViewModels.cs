using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Installation;
using DeployNest.Core.Settings;
using DeployNest.Linux.Services;
using Microsoft.Extensions.Logging;

namespace DeployNest.Linux.ViewModels;

/// <summary>A selected program on the review screen, with its source and verification result.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    private static readonly FailureDescriber Describer = new(Strings.Localizer, "Linux");
    private readonly Action _changed;

    public QueueItemViewModel(SoftwareItemViewModel software, InstallMethod? method, Core.Catalog.LinuxPackageManager? manager, bool reinstall, Action changed)
    {
        Software = software;
        Method = method;
        _changed = changed;
        _reinstall = reinstall;
        SourceText = ProgressItemViewModel.SourceText(software.Entry, method, manager);
    }

    public SoftwareItemViewModel Software { get; }
    public InstallMethod? Method { get; }
    public string SourceText { get; }
    public bool CanReinstall => Software.IsInstalled && Method is not null;

    [ObservableProperty]
    private PackageVerification? _verification;

    [ObservableProperty]
    private bool _reinstall;

    [ObservableProperty]
    private PlanItem? _plan;

    [ObservableProperty]
    private string _statusText = Strings.Get("Queue.Item.Verifying");

    [ObservableProperty]
    private PillKind _statusPill = PillKind.Neutral;

    [ObservableProperty]
    private string? _detailText;

    partial void OnReinstallChanged(bool value) => _changed();

    public void ApplyPlan(PlanItem plan)
    {
        Plan = plan;
        (StatusText, StatusPill) = plan.Decision switch
        {
            PlanDecision.Install when plan.AllowUpgrade => (Strings.Get("Queue.Item.Reinstall"), PillKind.Accent),
            PlanDecision.Install => (Strings.Get("Queue.Item.WillInstall"), PillKind.Accent),
            PlanDecision.SkipInstalled => (Strings.Get("Queue.Item.SkipInstalled"), PillKind.Warning),
            _ => (Describer.Describe(plan.Reason).Title, PillKind.Danger),
        };

        var parts = new List<string>();
        if (plan.Verification?.Version is { } version)
            parts.Add(Strings.Format("Queue.Item.Version", version));
        if (plan.SizeBytes is > 0 and var size)
            parts.Add("~" + LinuxCatalogState.FormatSize(size));
        if (plan.Decision == PlanDecision.Unavailable)
            parts.Add(Describer.Describe(plan.Reason).Advice);
        DetailText = parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}

public sealed partial class QueueViewModel : ObservableObject
{
    private readonly LinuxCatalogState _state;
    private readonly LinuxInstallController _controller;
    private readonly IPackageVerifier _verifier;
    private readonly ISettingsStore _settings;
    private readonly Navigator _navigator;
    private readonly ILogger<QueueViewModel> _logger;
    private CancellationTokenSource? _checkCts;
    private int _generation;

    public QueueViewModel(LinuxCatalogState state, LinuxInstallController controller, IPackageVerifier verifier, ISettingsStore settings, Navigator navigator, ILogger<QueueViewModel> logger)
    {
        _state = state;
        _controller = controller;
        _verifier = verifier;
        _settings = settings;
        _navigator = navigator;
        _logger = logger;
    }

    public LinuxCatalogState State => _state;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isChecking;

    [ObservableProperty]
    private string? _checkingText;

    [ObservableProperty]
    private string? _summaryText;

    [ObservableProperty]
    private string? _elevationText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _consentAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private int _installCount;

    [ObservableProperty]
    private bool _isEmpty = true;

    public bool CanStart => !IsChecking && ConsentAccepted && InstallCount > 0 && !_controller.IsRunning;

    /// <summary>Rebuilds the review list from the current selection and verifies every package.</summary>
    public void Refresh()
    {
        _checkCts?.Cancel();
        var generation = ++_generation;
        Items.Clear();
        var context = _state.CreatePlannerContext();
        var manager = _state.System?.PackageManager;
        var skipInstalled = _settings.Load().SkipInstalledByDefault;
        foreach (var software in _state.Selected)
        {
            var method = context.ChooseMethod(software.Entry);
            Items.Add(new QueueItemViewModel(software, method, manager, reinstall: software.IsInstalled && !skipInstalled, Rebuild));
        }
        IsEmpty = Items.Count == 0;
        InstallCount = 0;
        SummaryText = null;
        ElevationText = null;
        if (IsEmpty)
            return;

        _checkCts = new CancellationTokenSource();
        _ = VerifyAsync(generation, _checkCts.Token);
    }

    private async Task VerifyAsync(int generation, CancellationToken cancellationToken)
    {
        IsChecking = true;
        var done = 0;
        var items = Items.ToList();
        CheckingText = Strings.Format("Linux.Queue.Checking", 0, items.Count);
        using var gate = new SemaphoreSlim(4);
        try
        {
            await Task.WhenAll(items.Select(async item =>
            {
                if (item.Method is not { } method)
                {
                    item.Verification = null;
                }
                else
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        item.Verification = await Task.Run(() => _verifier.VerifyAsync(item.Software.Entry, method, cancellationToken), cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "{Id} doğrulanamadı", item.Software.Id);
                        item.Verification = PackageVerification.Failed(FailureKind.VerificationFailed, ex.Message);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                CheckingText = Strings.Format("Linux.Queue.Checking", Interlocked.Increment(ref done), items.Count);
            }));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (generation == _generation)
                IsChecking = false;
        }

        if (generation == _generation)
            Rebuild();
    }

    private InstallPlan BuildPlan() => InstallPlanner.Create(
        Items.Select(i => new PlannerInput(i.Software.Entry, i.Software.State, i.Reinstall, i.Verification)),
        _state.CreatePlannerContext(),
        _state.Catalog.Categories);

    private void Rebuild()
    {
        if (IsChecking)
            return;
        var plan = BuildPlan();
        foreach (var item in Items)
        {
            if (plan.Items.FirstOrDefault(p => p.Software.Id == item.Software.Id) is { } planItem)
                item.ApplyPlan(planItem);
        }

        var install = plan.ToInstall.ToList();
        InstallCount = install.Count;
        var size = plan.KnownDownloadBytes > 0 ? LinuxCatalogState.FormatSize(plan.KnownDownloadBytes) : Strings.Get("Common.Unknown");
        SummaryText = plan.UnknownSizeCount > 0 && plan.KnownDownloadBytes > 0
            ? Strings.Format("Queue.SummaryPartialSize", install.Count, Items.Count, size, plan.UnknownSizeCount)
            : Strings.Format("Queue.Summary", install.Count, Items.Count, size);
        var native = install.Count(i => i.Method == InstallMethod.NativePackage);
        ElevationText = native > 0 ? Strings.Format("Linux.Queue.Elevation", native) : Strings.Get("Linux.Queue.NoElevation");
    }

    [RelayCommand]
    private void Recheck() => Refresh();

    [RelayCommand]
    private void Remove(QueueItemViewModel item)
    {
        item.Software.IsSelected = false;
        Refresh();
    }

    [RelayCommand]
    private void AddMore() => _navigator.Go(PageKey.Programs);

    [RelayCommand]
    private async Task InstallFlatpak()
    {
        await _state.InstallFlatpakAsync();
        Refresh();
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart)
            return;
        var plan = BuildPlan();
        ConsentAccepted = false;
        var run = _controller.StartAsync(plan);
        _navigator.Go(PageKey.Install);
        await run;
    }
}

public sealed partial class ProgressViewModel(LinuxInstallController controller, Navigator navigator) : ObservableObject
{
    public LinuxInstallController Controller => controller;

    [ObservableProperty]
    private bool _confirmCancel;

    [RelayCommand]
    private void AskCancel() => ConfirmCancel = true;

    [RelayCommand]
    private void KeepRunning() => ConfirmCancel = false;

    [RelayCommand]
    private void Stop()
    {
        ConfirmCancel = false;
        controller.RequestStop();
    }

    [RelayCommand]
    private void Abort()
    {
        ConfirmCancel = false;
        controller.RequestAbort();
    }

    [RelayCommand]
    private void ViewResults() => navigator.Go(PageKey.Install);
}

public sealed partial class ResultsViewModel(LinuxInstallController controller, LinuxCatalogState state, Navigator navigator, IAppPaths paths) : ObservableObject
{
    public LinuxInstallController Controller => controller;

    public string Headline => controller.LastResult switch
    {
        null => Strings.Get("Results.None"),
        { CancelRequested: true } => Strings.Get("Results.Headline.Canceled"),
        var r when r.Failed.Any() => Strings.Format("Results.Headline.Partial", r.Failed.Count()),
        _ => Strings.Get("Results.Headline.Success"),
    };

    public string Subline => controller.LastResult is { } r
        ? Strings.Format("Results.Subline", r.StartedAt.LocalDateTime.ToString("g", Strings.Localizer.Culture), new ReportBuilder(Strings.Localizer).FormatDuration(r.Duration))
        : Strings.Get("Results.None.Hint");

    public int SucceededCount => controller.LastResult?.Succeeded.Count() ?? 0;
    public int FailedCount => controller.LastResult?.Failed.Count() ?? 0;
    public int SkippedCount => controller.LastResult?.Skipped.Count() ?? 0;
    public int CanceledCount => controller.LastResult?.Canceled.Count() ?? 0;
    public bool HasFailures => FailedCount > 0;

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (controller.LastReportPath is { } path)
            Shell.Open(path);
    }

    [RelayCommand]
    private void OpenLogs() => Shell.Open(paths.LogDirectory);

    [RelayCommand]
    private void RetryFailed()
    {
        if (controller.LastResult is not { } result)
            return;
        state.ClearSelection();
        state.SelectMany(result.Failed.Select(f => f.Software.Id));
        controller.Reset();
        navigator.Go(PageKey.Install);
    }

    [RelayCommand]
    private void Done()
    {
        state.ClearSelection();
        controller.Reset();
        navigator.Go(PageKey.Home);
    }
}
