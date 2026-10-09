using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Controls;
using DeployNest.App.Services;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Formatting;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.ViewModels;

public sealed record RequirementViewModel(string Title, string Detail, PillKind Kind, string Glyph);

/// <summary>One program on the confirmation ("Kurulum Kuyruğu") screen.</summary>
public sealed partial class QueueItemViewModel(SoftwareItemViewModel software, bool reinstall, ILocalizer loc, Action changed) : ObservableObject
{
    public SoftwareItemViewModel Software { get; } = software;
    public SoftwareEntry Entry => Software.Entry;
    public bool IsInstalled => Software.State == InstallState.Installed;

    [ObservableProperty]
    private bool _reinstall = reinstall;

    [ObservableProperty]
    private PackageVerification? _verification;

    [ObservableProperty]
    private bool _isVerifying = true;

    [ObservableProperty]
    private string _orderText = string.Empty;

    [ObservableProperty]
    private string _decisionText = loc["Queue.Item.Verifying"];

    [ObservableProperty]
    private PillKind _decisionKind = PillKind.Accent;

    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string? _detailText;

    [ObservableProperty]
    private PlanDecision? _decision;

    partial void OnReinstallChanged(bool value) => changed();

    public void ApplyPlan(PlanItem plan, int order, FailureDescriber describer)
    {
        Decision = plan.Decision;
        OrderText = plan.Decision == PlanDecision.Install ? order.ToString(loc.Culture) : "–";
        (DecisionText, DecisionKind) = plan.Decision switch
        {
            PlanDecision.Install when plan.AllowUpgrade => (loc["Queue.Item.Reinstall"], PillKind.Accent),
            PlanDecision.Install => (loc["Queue.Item.WillInstall"], PillKind.Success),
            PlanDecision.SkipInstalled => (loc["Queue.Item.SkipInstalled"], PillKind.Warning),
            _ => (describer.Describe(plan.Reason).Title, PillKind.Danger),
        };

        var method = plan.Method switch
        {
            InstallMethod.Winget => loc.Format("Item.Source.Winget", Entry.Winget!.PackageId),
            InstallMethod.Direct => loc.Format("Item.Source.Direct", Entry.Direct!.Url.Host),
            _ => loc["Method.None"],
        };
        var host = plan.Verification?.InstallerUrl?.Host;
        SourceText = host is null ? method : loc.Format("Queue.Item.SourceWithHost", method, host);

        var parts = new List<string>();
        if (plan.Verification?.Version is { } version)
            parts.Add(loc.Format("Queue.Item.Version", version));
        parts.Add(plan.SizeBytes is { } size ? SizeFormatter.Format(size, loc.Culture) : loc["Item.SizeUnknown"]);
        parts.Add(plan.Verification?.License ?? Software.License);
        if (plan.Decision == PlanDecision.Unavailable)
            parts.Add(describer.Describe(plan.Reason).Advice);
        DetailText = string.Join(" · ", parts);
    }
}

public sealed partial class QueueViewModel : ObservableObject, IPageViewModel
{
    private const long DiskMarginBytes = 1024L * 1024 * 1024;
    private const long UnknownSizeAllowanceBytes = 500L * 1024 * 1024;

    private readonly CatalogState _state;
    private readonly IPackageVerifier _verifier;
    private readonly ISystemProbe _probe;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly InstallSessionController _session;
    private readonly NavigationService _navigation;
    private readonly ShellService _shell;
    private readonly ILocalizer _loc;
    private readonly ILogger<QueueViewModel> _logger;
    private CancellationTokenSource? _preflightCts;
    private InstallPlan? _plan;
    private bool _internetOk;
    private bool _diskOk = true;

    public QueueViewModel(
        CatalogState state,
        IPackageVerifier verifier,
        ISystemProbe probe,
        ISettingsStore settings,
        IAppPaths paths,
        InstallSessionController session,
        NavigationService navigation,
        ShellService shell,
        ILocalizer localizer,
        ILogger<QueueViewModel> logger)
    {
        _state = state;
        _verifier = verifier;
        _probe = probe;
        _settings = settings;
        _paths = paths;
        _session = session;
        _navigation = navigation;
        _shell = shell;
        _loc = localizer;
        _logger = logger;
    }

    public PageKey Key => PageKey.Queue;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];
    public ObservableCollection<RequirementViewModel> Requirements { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isChecking;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _consentGiven;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string? _blockingMessage;

    [ObservableProperty]
    private bool _isEmpty;

    public void OnNavigatedTo(object? parameter)
    {
        if (_session.IsRunning)
        {
            _navigation.Navigate(PageKey.Progress);
            return;
        }
        _ = RunPreflightAsync();
    }

    [RelayCommand]
    private Task Recheck() => RunPreflightAsync();

    private async Task RunPreflightAsync()
    {
        _preflightCts?.Cancel();
        var cts = new CancellationTokenSource();
        _preflightCts = cts;
        var token = cts.Token;

        var settings = _settings.Load();
        Items.Clear();
        Requirements.Clear();
        Warnings.Clear();
        ConsentGiven = false;
        BlockingMessage = null;
        _plan = null;
        IsEmpty = _state.SelectedCount == 0;
        if (IsEmpty)
        {
            SummaryText = _loc["Queue.Empty"];
            return;
        }

        foreach (var software in _state.Selected.OrderBy(s => s.Entry.Order))
        {
            var reinstallDefault = software.State == InstallState.Installed && !settings.SkipInstalledByDefault;
            Items.Add(new QueueItemViewModel(software, reinstallDefault, _loc, RebuildPlan));
        }

        IsChecking = true;
        try
        {
            ProgressText = _loc["Queue.Checking.Requirements"];
            _internetOk = await _probe.IsInternetAvailableAsync(token);
            var wingetAvailable = await _state.WingetStatusOrDetectAsync();
            token.ThrowIfCancellationRequested();

            var done = 0;
            using var limiter = new SemaphoreSlim(3);
            var tasks = Items.Select(async item =>
            {
                await limiter.WaitAsync(token);
                try
                {
                    var method = InstallPlanner.ChooseMethod(item.Entry, wingetAvailable);
                    if (method is not null && _internetOk)
                    {
                        item.Verification = await _verifier.VerifyAsync(item.Entry, method.Value, token);
                        if (item.Verification.Verified)
                            _state.UpdatePackageInfo(item.Software, item.Verification.Version, item.Verification.SizeBytes);
                    }
                    else if (method is not null)
                    {
                        item.Verification = PackageVerification.Failed(FailureKind.NetworkUnavailable);
                    }
                }
                finally
                {
                    item.IsVerifying = false;
                    limiter.Release();
                    ProgressText = _loc.Format("Queue.Checking.Packages", Interlocked.Increment(ref done), Items.Count);
                }
            }).ToList();
            await Task.WhenAll(tasks);
            token.ThrowIfCancellationRequested();

            RebuildPlan();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kurulum öncesi denetim başarısız");
            BlockingMessage = _loc["Queue.PreflightFailed"];
        }
        finally
        {
            if (_preflightCts == cts)
                IsChecking = false;
        }
    }

    private void RebuildPlan()
    {
        if (IsChecking && Items.Any(i => i.IsVerifying))
            return;

        var wingetAvailable = _state.WingetAvailable;
        var plan = InstallPlanner.Create(
            Items.Select(i => new PlannerInput(i.Entry, i.Software.State, i.Reinstall, i.Verification)),
            wingetAvailable,
            _state.Catalog.Categories);
        _plan = plan;

        var describer = new FailureDescriber(_loc);
        var order = 0;
        var ordered = new List<QueueItemViewModel>();
        foreach (var planItem in plan.Items)
        {
            var vm = Items.First(i => i.Entry.Id == planItem.Software.Id);
            vm.ApplyPlan(planItem, planItem.Decision == PlanDecision.Install ? ++order : 0, describer);
            ordered.Add(vm);
        }
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Items.IndexOf(ordered[i]);
            if (current != i)
                Items.Move(current, i);
        }

        BuildRequirements(plan, wingetAvailable);
        BuildWarnings(plan);

        var toInstall = plan.ToInstall.Count();
        var size = plan.KnownDownloadBytes > 0 ? SizeFormatter.Format(plan.KnownDownloadBytes, _loc.Culture) : _loc["Common.Unknown"];
        SummaryText = plan.UnknownSizeCount > 0 && plan.KnownDownloadBytes > 0
            ? _loc.Format("Queue.SummaryPartialSize", toInstall, Items.Count, size, plan.UnknownSizeCount)
            : _loc.Format("Queue.Summary", toInstall, Items.Count, size);

        BlockingMessage = !_internetOk ? _loc["Queue.Block.Offline"]
            : !_diskOk ? _loc["Queue.Block.Disk"]
            : toInstall == 0 ? _loc["Queue.Block.Nothing"]
            : null;
        StartCommand.NotifyCanExecuteChanged();
    }

    private void BuildRequirements(InstallPlan plan, bool wingetAvailable)
    {
        Requirements.Clear();
        Requirements.Add(_internetOk
            ? new RequirementViewModel(_loc["Queue.Req.Internet"], _loc["Queue.Req.Internet.Ok"], PillKind.Success, "")
            : new RequirementViewModel(_loc["Queue.Req.Internet"], _loc["Queue.Req.Internet.Missing"], PillKind.Danger, ""));

        Requirements.Add(wingetAvailable
            ? new RequirementViewModel(_loc["Queue.Req.Winget"], _state.WingetStatusText, PillKind.Success, "")
            : new RequirementViewModel(_loc["Queue.Req.Winget"], _loc["Queue.Req.Winget.Missing"], PillKind.Warning, ""));

        var settings = _settings.Load();
        var downloadDir = settings.DownloadDirectory ?? _paths.DefaultDownloadDirectory;
        var needed = plan.KnownDownloadBytes + plan.UnknownSizeCount * UnknownSizeAllowanceBytes + DiskMarginBytes;
        var tempFree = _probe.GetFreeSpace(Path.GetTempPath());
        var downloadFree = _probe.GetFreeSpace(downloadDir);
        var free = new[] { tempFree, downloadFree }.Where(f => f.HasValue).Select(f => f!.Value).DefaultIfEmpty(long.MaxValue).Min();
        _diskOk = free >= needed;
        var freeText = free == long.MaxValue ? _loc["Common.Unknown"] : SizeFormatter.Format(free, _loc.Culture);
        Requirements.Add(_diskOk
            ? new RequirementViewModel(_loc["Queue.Req.Disk"], _loc.Format("Queue.Req.Disk.Ok", freeText), PillKind.Success, "")
            : new RequirementViewModel(_loc["Queue.Req.Disk"], _loc.Format("Queue.Req.Disk.Low", freeText, SizeFormatter.Format(needed, _loc.Culture)), PillKind.Danger, ""));

        var adminLikely = plan.ToInstall.Count(i => i.Software.Elevation != ElevationHint.Unlikely);
        Requirements.Add(adminLikely > 0
            ? new RequirementViewModel(_loc["Queue.Req.Admin"], _loc.Format("Queue.Req.Admin.Some", adminLikely), PillKind.Warning, "")
            : new RequirementViewModel(_loc["Queue.Req.Admin"], _loc["Queue.Req.Admin.None"], PillKind.Success, ""));
    }

    private void BuildWarnings(InstallPlan plan)
    {
        Warnings.Clear();
        var install = plan.ToInstall.ToList();

        var interactive = install.Count(i => !i.RunsUnattended);
        if (interactive > 0)
            Warnings.Add(_loc.Format("Queue.Warn.Interactive", interactive));

        var skipped = plan.Items.Count(i => i.Decision == PlanDecision.SkipInstalled);
        if (skipped > 0)
            Warnings.Add(_loc.Format("Queue.Warn.Skipped", skipped));

        var unavailable = plan.Items.Count(i => i.Decision == PlanDecision.Unavailable);
        if (unavailable > 0)
            Warnings.Add(_loc.Format("Queue.Warn.Unavailable", unavailable));

        if (install.Any(i => i.Method == InstallMethod.Direct))
            Warnings.Add(_loc["Queue.Warn.Direct"]);

        foreach (var item in install)
        {
            if (item.Software.Notes?.Get(_loc.Language) is { Length: > 0 } note)
                Warnings.Add($"{item.Software.Name}: {note}");
            foreach (var warning in item.Verification?.Warnings ?? [])
            {
                if (warning.StartsWith("dependency:", StringComparison.Ordinal))
                    Warnings.Add(_loc.Format("Queue.Warn.Dependency", item.Software.Name, warning["dependency:".Length..]));
                else if (warning == "publisherUnreadable")
                    Warnings.Add(_loc.Format("Queue.Warn.PublisherUnreadable", item.Software.Name));
                else if (warning == "installerNotHttps")
                    Warnings.Add(_loc.Format("Queue.Warn.NotHttps", item.Software.Name));
            }
            foreach (var agreement in item.Verification?.Agreements ?? [])
                Warnings.Add($"{item.Software.Name}: {agreement}");
        }

        if (install.Count > 0)
            Warnings.Add(_loc["Queue.Warn.General"]);
    }

    private bool CanStart() =>
        !IsChecking && ConsentGiven && _plan is not null && _plan.ToInstall.Any() && _internetOk && _diskOk && !_session.IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (_plan is null)
            return;
        var plan = _plan;
        _plan = null;
        StartCommand.NotifyCanExecuteChanged();

        var run = _session.StartAsync(plan);
        _navigation.Navigate(PageKey.Progress);
        try
        {
            await run;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kurulum oturumu başlatılamadı");
        }
    }

    [RelayCommand]
    private void OpenLicense(QueueItemViewModel item) =>
        _shell.OpenUrl(item.Verification?.LicenseUrl ?? item.Software.LicenseUrl);

    [RelayCommand]
    private void Back() => _navigation.Navigate(PageKey.Selection);
}
