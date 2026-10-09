using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeployNest.App.Controls;
using DeployNest.App.Services;
using DeployNest.Core.Abstractions;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using DeployNest.Infrastructure.Catalog;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.ViewModels;

public sealed record HistoryRowViewModel(
    string Name,
    string DateText,
    string Operation,
    string ResultText,
    PillKind Kind,
    string? Message,
    string DurationText,
    string MethodText);

public sealed partial class HistoryViewModel(
    IHistoryStore history,
    DialogService dialogs,
    ToastService toasts,
    ILocalizer loc,
    ILogger<HistoryViewModel> logger) : ObservableObject, IPageViewModel
{
    public PageKey Key => PageKey.History;

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    public void OnNavigatedTo(object? parameter) => Load();

    private void Load()
    {
        Rows.Clear();
        var report = new ReportBuilder(loc);
        var entries = history.Load();
        foreach (var entry in entries)
        {
            Rows.Add(new HistoryRowViewModel(
                entry.SoftwareName,
                entry.Timestamp.LocalDateTime.ToString("g", loc.Culture),
                loc[$"Operation.{entry.Operation}"],
                loc[$"Status.{entry.Status}"],
                entry.Status switch
                {
                    OutcomeStatus.Succeeded => PillKind.Success,
                    OutcomeStatus.Failed => PillKind.Danger,
                    _ => PillKind.Warning,
                },
                entry.Message,
                entry.Duration > TimeSpan.Zero ? report.FormatDuration(entry.Duration) : "–",
                entry.Method switch
                {
                    InstallMethod.Winget => loc["Method.Winget"],
                    InstallMethod.Direct => loc["Method.Direct"],
                    _ => "–",
                }));
        }
        IsEmpty = Rows.Count == 0;
        Summary = IsEmpty ? string.Empty : loc.Format("History.Summary", Rows.Count, entries.Select(e => e.SessionId).Distinct().Count());
        ClearCommand.NotifyCanExecuteChanged();
    }

    private bool CanClear() => !IsEmpty;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task Clear()
    {
        var answer = await dialogs.ShowAsync(new DialogRequest
        {
            Title = loc["History.Clear.Title"],
            Message = loc["History.Clear.Message"],
            PrimaryText = loc["History.Clear.Confirm"],
            CloseText = loc["Common.Cancel"],
            PrimaryIsDanger = true,
        });
        if (answer != DialogResult.Primary)
            return;
        try
        {
            history.Clear();
            toasts.Show(loc["History.Cleared"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Geçmiş temizlenemedi");
            toasts.Show(loc["History.ClearFailed"]);
        }
        Load();
    }
}

public sealed record ThemeOption(ThemePreference Value, string Name);

public sealed partial class SettingsViewModel : ObservableObject, IPageViewModel
{
    private readonly ISettingsStore _store;
    private readonly ThemeService _theme;
    private readonly IAppPaths _paths;
    private readonly FileDialogService _files;
    private readonly ShellService _shell;
    private readonly IUpdateChecker _updates;
    private readonly ILocalizer _loc;
    private bool _loading;

    public SettingsViewModel(
        ISettingsStore store,
        ThemeService theme,
        IAppPaths paths,
        FileDialogService files,
        ShellService shell,
        IUpdateChecker updates,
        ILocalizer localizer)
    {
        _store = store;
        _theme = theme;
        _paths = paths;
        _files = files;
        _shell = shell;
        _updates = updates;
        _loc = localizer;
        ThemeOptions =
        [
            new(ThemePreference.System, localizer["Settings.Theme.System"]),
            new(ThemePreference.Light, localizer["Settings.Theme.Light"]),
            new(ThemePreference.Dark, localizer["Settings.Theme.Dark"]),
        ];
        Languages = localizer.AvailableLanguages;
        _selectedTheme = ThemeOptions[0];
        _selectedLanguage = Languages[0];

        // The sidebar toggle can change the theme too; keep this page in sync without saving again.
        theme.ThemeChanged += (_, _) =>
        {
            _loading = true;
            SelectedTheme = ThemeOptions.First(t => t.Value == _theme.Preference);
            _loading = false;
        };
    }

    public PageKey Key => PageKey.Settings;

    public IReadOnlyList<ThemeOption> ThemeOptions { get; }
    public IReadOnlyList<LanguageInfo> Languages { get; }
    public IReadOnlyList<int> ParallelOptions { get; } = [1, 2, 3, 4];
    public string LogDirectory => _paths.LogDirectory;
    public string VersionText => _loc.Format("Settings.Version", AppInfo.Version);
    public string CatalogText => _loc.Format("Settings.Catalog", CatalogProvider.ExternalCatalogPath);

    [ObservableProperty]
    private ThemeOption _selectedTheme;

    [ObservableProperty]
    private LanguageInfo _selectedLanguage;

    [ObservableProperty]
    private string _downloadDirectory = string.Empty;

    [ObservableProperty]
    private bool _isCustomDownloadDirectory;

    [ObservableProperty]
    private int _maxParallelDownloads = 2;

    [ObservableProperty]
    private bool _keepDownloadedFiles;

    [ObservableProperty]
    private bool _skipInstalledByDefault = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string? _updateText;

    [ObservableProperty]
    private Uri? _updateUrl;

    public void OnNavigatedTo(object? parameter)
    {
        _loading = true;
        var settings = _store.Load();
        SelectedTheme = ThemeOptions.First(t => t.Value == settings.Theme);
        SelectedLanguage = Languages.FirstOrDefault(l => l.Code == settings.Language) ?? Languages[0];
        DownloadDirectory = settings.DownloadDirectory ?? _paths.DefaultDownloadDirectory;
        IsCustomDownloadDirectory = settings.DownloadDirectory is not null;
        MaxParallelDownloads = settings.MaxParallelDownloads;
        KeepDownloadedFiles = settings.KeepDownloadedFiles;
        SkipInstalledByDefault = settings.SkipInstalledByDefault;
        _loading = false;
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (_loading || value is null || value.Value == _theme.Preference)
            return;
        _theme.Apply(value.Value);
        Save(s => s with { Theme = value.Value });
    }

    partial void OnSelectedLanguageChanged(LanguageInfo value)
    {
        if (_loading || value is null || value.Code == _loc.Language)
            return;
        _loc.SetLanguage(value.Code);
        Save(s => s with { Language = value.Code });
    }

    partial void OnMaxParallelDownloadsChanged(int value)
    {
        if (!_loading && value >= AppSettings.MinParallelDownloads)
            Save(s => s with { MaxParallelDownloads = value });
    }

    partial void OnKeepDownloadedFilesChanged(bool value)
    {
        if (!_loading)
            Save(s => s with { KeepDownloadedFiles = value });
    }

    partial void OnSkipInstalledByDefaultChanged(bool value)
    {
        if (!_loading)
            Save(s => s with { SkipInstalledByDefault = value });
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        var current = _store.Load();
        var updated = change(current);
        if (updated != current)
            _store.Save(updated);
    }

    [RelayCommand]
    private void ChangeDownloadFolder()
    {
        var folder = _files.PickFolder(DownloadDirectory, _loc["Settings.Downloads.Pick"]);
        if (folder is null)
            return;
        DownloadDirectory = folder;
        IsCustomDownloadDirectory = true;
        Save(s => s with { DownloadDirectory = folder });
    }

    [RelayCommand]
    private void ResetDownloadFolder()
    {
        DownloadDirectory = _paths.DefaultDownloadDirectory;
        IsCustomDownloadDirectory = false;
        Save(s => s with { DownloadDirectory = null });
    }

    [RelayCommand]
    private void OpenDownloadFolder() => _shell.OpenFolder(DownloadDirectory);

    [RelayCommand]
    private void OpenLogs() => _shell.OpenFolder(_paths.LogDirectory);

    [RelayCommand]
    private void OpenDataFolder() => _shell.OpenFolder(_paths.DataDirectory);

    private bool CanCheckUpdates() => !IsCheckingUpdates;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdates()
    {
        IsCheckingUpdates = true;
        UpdateText = _loc["Settings.Update.Checking"];
        UpdateUrl = null;
        try
        {
            var result = await _updates.CheckAsync(AppInfo.AssemblyVersion, CancellationToken.None);
            UpdateUrl = result.ReleaseUrl;
            UpdateText = result switch
            {
                { Succeeded: false } => _loc["Settings.Update.Failed"],
                { Message: "noReleases" } => _loc["Settings.Update.NoReleases"],
                { UpdateAvailable: true } => _loc.Format("Settings.Update.Available", result.LatestVersion),
                _ => _loc.Format("Settings.Update.UpToDate", AppInfo.Version),
            };
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private void OpenUpdate() => _shell.OpenUrl(UpdateUrl);
}

public sealed record ThirdPartyComponent(string Name, string License, string Usage);

public sealed partial class AboutViewModel(ShellService shell, ILocalizer loc) : ObservableObject, IPageViewModel
{
    public PageKey Key => PageKey.About;

    public string VersionText => loc.Format("Settings.Version", AppInfo.Version);

    public IReadOnlyList<ThirdPartyComponent> Components { get; } =
    [
        new(".NET 10 / WPF", "MIT", "Microsoft"),
        new("CommunityToolkit.Mvvm", "MIT", ".NET Foundation"),
        new("Microsoft.Extensions.DependencyInjection / Logging", "MIT", "Microsoft"),
        new("Windows Package Manager (winget)", "MIT", "Microsoft — Windows ile gelir, uygulamaya dahil edilmez"),
    ];

    public void OnNavigatedTo(object? parameter)
    {
    }

    [RelayCommand]
    private void OpenRepository() => shell.OpenUrl(AppInfo.RepositoryUrl);

    [RelayCommand]
    private void OpenReleases() => shell.OpenUrl(AppInfo.ReleasesUrl);

    [RelayCommand]
    private void OpenIssues() => shell.OpenUrl(AppInfo.IssuesUrl);

    [RelayCommand]
    private void OpenWingetDocs() => shell.OpenUrl(AppInfo.WingetDocsUrl);
}
