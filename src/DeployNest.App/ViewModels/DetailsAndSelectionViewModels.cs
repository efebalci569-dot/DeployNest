using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Controls;
using DeployNest.App.Services;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Detection;
using DeployNest.Core.Formatting;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;

namespace DeployNest.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed partial class ProgramDetailsViewModel(
    CatalogState state,
    NavigationService navigation,
    IPackageVerifier verifier,
    ShellService shell,
    ILocalizer loc) : ObservableObject, IPageViewModel
{
    private CancellationTokenSource? _verifyCts;

    public PageKey Key => PageKey.Details;

    public CatalogState State => state;

    [ObservableProperty]
    private SoftwareItemViewModel? _item;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(VerifyCommand))]
    private bool _isVerifying;

    [ObservableProperty]
    private string? _verificationText;

    [ObservableProperty]
    private PillKind _verificationKind = PillKind.Neutral;

    [ObservableProperty]
    private IReadOnlyList<InfoRow> _verificationRows = [];

    public IReadOnlyList<InfoRow> InfoRows => Item is null ? [] :
    [
        new(loc["Details.Publisher"], Item.Publisher),
        new(loc["Details.Category"], Item.CategoryName),
        new(loc["Details.Source"], Item.SourceText),
        new(loc["Details.License"], Item.License),
        new(loc["Details.Elevation"], Item.ElevationText),
        new(loc["Details.Fallback"], Item.HasDirectFallback ? loc["Details.Fallback.Yes"] : loc["Details.Fallback.No"]),
        new(loc["Details.Size"], Item.SizeText),
    ];

    public void OnNavigatedTo(object? parameter)
    {
        _verifyCts?.Cancel();
        if (parameter is SoftwareItemViewModel item)
            Item = item;
        IsVerifying = false;
        VerificationText = null;
        VerificationRows = [];
        OnPropertyChanged(nameof(InfoRows));
    }

    [RelayCommand]
    private void Back() => navigation.GoBack();

    [RelayCommand]
    private void ToggleSelection()
    {
        if (Item is not null)
            Item.IsSelected = !Item.IsSelected;
    }

    [RelayCommand]
    private void OpenHomepage() => shell.OpenUrl(Item?.Homepage);

    [RelayCommand]
    private void OpenLicense() => shell.OpenUrl(Item?.LicenseUrl);

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        if (Item is not { } item)
            return;

        _verifyCts?.Cancel();
        _verifyCts = new CancellationTokenSource();
        IsVerifying = true;
        VerificationText = loc["Details.Verify.Running"];
        VerificationKind = PillKind.Accent;
        VerificationRows = [];
        try
        {
            var winget = await state.WingetStatusOrDetectAsync();
            var method = InstallPlanner.ChooseMethod(item.Entry, winget);
            if (method is null)
            {
                VerificationText = new FailureDescriber(loc).Describe(FailureKind.WingetMissing).Title;
                VerificationKind = PillKind.Warning;
                return;
            }

            var result = await verifier.VerifyAsync(item.Entry, method.Value, _verifyCts.Token);
            if (result.Verified)
            {
                state.UpdatePackageInfo(item, result.Version, result.SizeBytes);
                VerificationText = loc[method == InstallMethod.Winget ? "Details.Verify.OkWinget" : "Details.Verify.OkDirect"];
                VerificationKind = PillKind.Success;
                VerificationRows =
                [
                    new(loc["Details.Verify.Version"], result.Version ?? loc["Common.Unknown"]),
                    new(loc["Details.Verify.Publisher"], result.Publisher ?? loc["Common.Unknown"]),
                    new(loc["Details.Verify.Server"], result.InstallerUrl?.Host ?? loc["Common.Unknown"]),
                    new(loc["Details.Verify.Type"], result.InstallerType ?? loc["Common.Unknown"]),
                    new(loc["Details.Size"], result.SizeBytes is { } size ? SizeFormatter.Format(size, loc.Culture) : loc["Item.SizeUnknown"]),
                ];
                OnPropertyChanged(nameof(InfoRows));
            }
            else
            {
                var description = new FailureDescriber(loc).Describe(result.Problem);
                VerificationText = $"{description.Title}. {description.Advice}";
                VerificationKind = PillKind.Danger;
            }
        }
        catch (OperationCanceledException)
        {
            VerificationText = null;
        }
        finally
        {
            IsVerifying = false;
        }
    }

    private bool CanVerify() => !IsVerifying;

    [RelayCommand]
    private void InstallNow()
    {
        if (Item is null)
            return;
        Item.IsSelected = true;
        navigation.Navigate(PageKey.Queue);
    }
}

public sealed partial class SelectionViewModel : ObservableObject, IPageViewModel
{
    private readonly NavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly InstallSessionController _session;
    private readonly ILocalizer _loc;

    public SelectionViewModel(CatalogState state, NavigationService navigation, DialogService dialogs, InstallSessionController session, ILocalizer localizer)
    {
        State = state;
        _navigation = navigation;
        _dialogs = dialogs;
        _session = session;
        _loc = localizer;
        state.SelectionChanged += (_, _) => Update();
        state.DetectionUpdated += (_, _) => Update();
    }

    public PageKey Key => PageKey.Selection;

    public CatalogState State { get; }

    [ObservableProperty]
    private string? _installedWarning;

    [ObservableProperty]
    private string _summary = string.Empty;

    public void OnNavigatedTo(object? parameter) => Update();

    private void Update()
    {
        var installed = State.Selected.Count(i => i.State == InstallState.Installed);
        InstalledWarning = installed > 0 ? _loc.Format("Selection.InstalledWarning", installed) : null;
        Summary = State.SelectedCount == 0
            ? _loc["Selection.Empty"]
            : _loc.Format("Selection.Summary", State.SelectedCount, State.SelectedSizeText);
    }

    [RelayCommand]
    private void Remove(SoftwareItemViewModel item) => item.IsSelected = false;

    [RelayCommand]
    private void OpenDetails(SoftwareItemViewModel item) => _navigation.Navigate(PageKey.Details, item);

    [RelayCommand]
    private async Task ClearAll()
    {
        if (State.SelectedCount == 0)
            return;
        var answer = await _dialogs.ShowAsync(new DialogRequest
        {
            Title = _loc["Selection.ClearConfirm.Title"],
            Message = _loc.Format("Selection.ClearConfirm.Message", State.SelectedCount),
            PrimaryText = _loc["Selection.ClearConfirm.Yes"],
            CloseText = _loc["Common.Cancel"],
            PrimaryIsDanger = true,
        });
        if (answer == DialogResult.Primary)
            State.ClearSelection();
    }

    [RelayCommand]
    private void Browse() => _navigation.Navigate(PageKey.Programs, new ProgramsQuery());

    [RelayCommand]
    private void StartInstall() => _navigation.Navigate(_session.IsRunning ? PageKey.Progress : PageKey.Queue);
}
