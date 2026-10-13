using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;
using DeployNest.Infrastructure.Linux;
using DeployNest.Linux.Services;

namespace DeployNest.Linux.ViewModels;

/// <summary>One row of the installation progress and results screens.</summary>
public sealed partial class ProgressItemViewModel : ObservableObject
{
    private static readonly FailureDescriber Describer = new(Strings.Localizer, "Linux");

    public ProgressItemViewModel(SoftwareItemViewModel software, PlanItem plan, LinuxPackageManager? manager)
    {
        Software = software;
        Plan = plan;
        Manager = manager;
        MethodText = SourceText(plan.Software, plan.Method, manager);
        _phaseText = Strings.Get("Phase.Pending");
    }

    public SoftwareItemViewModel Software { get; }
    public PlanItem Plan { get; }
    public LinuxPackageManager? Manager { get; }
    public string Id => Software.Id;
    public string MethodText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsDone), nameof(StatusPill))]
    private ItemPhase _phase = ItemPhase.Pending;

    [ObservableProperty]
    private string _phaseText;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isIndeterminate;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _advice;

    [ObservableProperty]
    private string? _manualCommand;

    public bool IsActive => Phase is ItemPhase.Preparing or ItemPhase.Downloading or ItemPhase.Verifying or ItemPhase.Installing or ItemPhase.Finalizing;

    public bool IsDone => Phase is ItemPhase.Succeeded or ItemPhase.Failed or ItemPhase.Skipped or ItemPhase.Canceled;

    public PillKind StatusPill => Phase switch
    {
        ItemPhase.Succeeded => PillKind.Success,
        ItemPhase.Failed => PillKind.Danger,
        ItemPhase.Skipped or ItemPhase.Canceled => PillKind.Warning,
        ItemPhase.Pending => PillKind.Neutral,
        _ => PillKind.Accent,
    };

    public static string SourceText(SoftwareEntry entry, InstallMethod? method, LinuxPackageManager? manager) => method switch
    {
        InstallMethod.Flatpak => Strings.Format("Linux.Source.Flatpak", entry.Linux?.FlatpakId),
        InstallMethod.NativePackage when manager is { } m => Strings.Format("Linux.Source.Native", m.ToString().ToLowerInvariant(), string.Join(' ', entry.Linux!.PackagesFor(m))),
        _ => Strings.Get("Linux.Source.None"),
    };

    public void Apply(ItemProgress progress)
    {
        Phase = progress.Phase;
        PhaseText = Strings.Get($"Phase.{progress.Phase}");
        if (progress.Percent is { } percent)
        {
            Percent = percent;
            IsIndeterminate = false;
        }
        else
        {
            IsIndeterminate = IsActive;
        }
    }

    public void MarkCanceling()
    {
        if (!IsDone && !IsActive)
            PhaseText = Strings.Get("Phase.CancelPending");
    }

    public void Complete(ItemResult result)
    {
        Phase = result.Outcome.Status switch
        {
            OutcomeStatus.Succeeded => ItemPhase.Succeeded,
            OutcomeStatus.Skipped => ItemPhase.Skipped,
            OutcomeStatus.Canceled => ItemPhase.Canceled,
            _ => ItemPhase.Failed,
        };
        PhaseText = Strings.Get($"Phase.{Phase}");
        IsIndeterminate = false;
        Percent = Phase == ItemPhase.Succeeded ? 100 : Percent;
        ResultText = Describer.Summarize(result.Outcome);
        if (result.Outcome.Status is OutcomeStatus.Failed or OutcomeStatus.Skipped)
            Advice = Describer.Describe(result.Outcome.Failure).Advice;
        if (result.Outcome.Status == OutcomeStatus.Failed && result.Method == InstallMethod.NativePackage && Manager is { } manager)
            ManualCommand = NativePackageStrategy.ManualCommand(manager, Software.Entry.Linux!.PackagesFor(manager), null);
        else if (result.Outcome.Status == OutcomeStatus.Failed && result.Method == InstallMethod.Flatpak)
            ManualCommand = $"flatpak install --user flathub {Software.Entry.Linux?.FlatpakId}";
    }
}
