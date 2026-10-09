using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.App.Controls;
using DeployNest.Core.Formatting;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;

namespace DeployNest.App.ViewModels;

/// <summary>One row of the installation progress and results screens.</summary>
public sealed partial class ProgressItemViewModel : ObservableObject
{
    private readonly ILocalizer _loc;

    public ProgressItemViewModel(SoftwareItemViewModel software, PlanItem plan, ILocalizer localizer)
    {
        Software = software;
        Plan = plan;
        _loc = localizer;
        _phase = ItemPhase.Pending;
        _phaseText = localizer["Phase.Pending"];
        MethodText = plan.Method switch
        {
            InstallMethod.Winget => _loc.Format("Item.Source.Winget", plan.Software.Winget!.PackageId),
            InstallMethod.Direct => _loc.Format("Item.Source.Direct", plan.Software.Direct!.Url.Host),
            _ => _loc["Method.None"],
        };
    }

    public SoftwareItemViewModel Software { get; }
    public PlanItem Plan { get; }
    public string Id => Software.Id;
    public string MethodText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone), nameof(StatusKind), nameof(StatusGlyph))]
    private ItemPhase _phase;

    [ObservableProperty]
    private string _phaseText;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string? _bytesText;

    [ObservableProperty]
    private ItemResult? _result;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _advice;

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private string? _durationText;

    public bool IsActive => Phase is ItemPhase.Preparing or ItemPhase.Downloading or ItemPhase.Verifying or ItemPhase.Installing or ItemPhase.Finalizing;

    public bool IsDone => Phase is ItemPhase.Succeeded or ItemPhase.Failed or ItemPhase.Skipped or ItemPhase.Canceled;

    public PillKind StatusKind => Phase switch
    {
        ItemPhase.Succeeded => PillKind.Success,
        ItemPhase.Failed => PillKind.Danger,
        ItemPhase.Skipped or ItemPhase.Canceled => PillKind.Warning,
        ItemPhase.Pending => PillKind.Neutral,
        _ => PillKind.Accent,
    };

    public string StatusGlyph => Phase switch
    {
        ItemPhase.Succeeded => "",
        ItemPhase.Failed => "",
        ItemPhase.Skipped => "",
        ItemPhase.Canceled => "",
        ItemPhase.Pending => "",
        _ => "",
    };

    public void Apply(ItemProgress progress)
    {
        if (IsDone)
            return;

        Phase = progress.Phase;
        PhaseText = progress.Phase == ItemPhase.Downloading && progress.Detail is { Length: > 0 } host
            ? _loc.Format("Phase.DownloadingFrom", host)
            : _loc[$"Phase.{progress.Phase}"];

        if (progress.Percent is { } percent)
        {
            Percent = percent;
            IsIndeterminate = false;
        }
        else
        {
            IsIndeterminate = IsActive;
        }

        BytesText = progress.BytesReceived is { } received
            ? progress.TotalBytes is { } total and > 0
                ? $"{SizeFormatter.Format(received, _loc.Culture)} / {SizeFormatter.Format(total, _loc.Culture)}"
                : SizeFormatter.Format(received, _loc.Culture)
            : progress.Phase == ItemPhase.Downloading ? BytesText : null;
    }

    public void MarkCanceling()
    {
        if (Phase == ItemPhase.Pending)
            PhaseText = _loc["Phase.CancelPending"];
    }

    public void Complete(ItemResult result)
    {
        Result = result;
        var describer = new FailureDescriber(_loc);
        Phase = result.Outcome.Status switch
        {
            OutcomeStatus.Succeeded => ItemPhase.Succeeded,
            OutcomeStatus.Skipped => ItemPhase.Skipped,
            OutcomeStatus.Canceled => ItemPhase.Canceled,
            _ => ItemPhase.Failed,
        };
        PhaseText = _loc[$"Phase.{Phase}"];
        IsIndeterminate = false;
        Percent = result.Outcome.Status == OutcomeStatus.Succeeded ? 100 : Percent;
        BytesText = null;
        ResultText = describer.Summarize(result.Outcome);
        Advice = result.Outcome.Status == OutcomeStatus.Succeeded
            ? result.PostInstallVerified == false ? _loc["Report.NotVerifiedAfterInstall"] : null
            : describer.Describe(result.Outcome.Failure).Advice;
        Notes = result.Notes.Count > 0 ? string.Join(Environment.NewLine, result.Notes) : null;
        DurationText = result.Duration > TimeSpan.Zero ? new ReportBuilder(_loc).FormatDuration(result.Duration) : null;
    }
}
