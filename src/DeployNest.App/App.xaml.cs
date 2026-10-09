using System.Windows;
using System.Windows.Threading;
using DeployNest.App.Localization;
using DeployNest.App.Services;
using DeployNest.App.ViewModels;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Detection;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using DeployNest.Infrastructure.Catalog;
using DeployNest.Infrastructure.Detection;
using DeployNest.Infrastructure.Direct;
using DeployNest.Infrastructure.Downloads;
using DeployNest.Infrastructure.Logging;
using DeployNest.Infrastructure.Platform;
using DeployNest.Infrastructure.Processes;
using DeployNest.Infrastructure.Security;
using DeployNest.Infrastructure.Storage;
using DeployNest.Infrastructure.Updates;
using DeployNest.Infrastructure.Verification;
using DeployNest.Infrastructure.Winget;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeployNest.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private ILogger<App>? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Local\DeployNest.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(new JsonLocalizer()["App.AlreadyRunning"], "DeployNest", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var paths = new AppPaths();
        paths.EnsureCreated();
        var loggerProvider = new FileLoggerProvider(paths.LogDirectory);

        _services = ConfigureServices(paths, loggerProvider);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("DeployNest {Version} başlatıldı ({Os})", AppInfo.Version, Environment.OSVersion.VersionString);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.LogError(args.Exception, "Gözlemlenmeyen görev hatası");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger.LogCritical(args.ExceptionObject as Exception, "İşlenmeyen hata");

        var settings = _services.GetRequiredService<ISettingsStore>().Load();
        var localizer = _services.GetRequiredService<ILocalizer>();
        localizer.SetLanguage(settings.Language);
        LocalizationSource.Instance.Attach(localizer);
        _services.GetRequiredService<ThemeService>().Apply(settings.Theme);

        var state = _services.GetRequiredService<CatalogState>();
        state.Initialize();

        var window = new MainWindow(_services.GetRequiredService<MainViewModel>(), _services.GetRequiredService<ThemeService>());
        MainWindow = window;
        window.Show();
        _services.GetRequiredService<NavigationService>().Navigate(PageKey.Home);

        _ = state.RefreshDetectionAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("DeployNest kapatıldı");
        _services?.Dispose();
        if (_singleInstance is not null)
        {
            try { _singleInstance.ReleaseMutex(); }
            catch (ApplicationException) { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    private static ServiceProvider ConfigureServices(AppPaths paths, FileLoggerProvider loggerProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(loggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Core / infrastructure
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ILocalizer>(_ => new JsonLocalizer());
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISystemProbe, SystemProbe>();
        services.AddSingleton(_ => HttpClientFactory.Create(AppInfo.Version));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IWingetLocator, WingetLocator>();
        services.AddSingleton<WingetClient>();
        services.AddSingleton<IWingetInventorySource>(sp => sp.GetRequiredService<WingetClient>());
        services.AddSingleton<IInstalledProgramSource, RegistryInstalledProgramSource>();
        services.AddSingleton<InstallationDetector>();
        services.AddSingleton<CatalogProvider>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IHistoryStore, JsonHistoryStore>();
        services.AddSingleton<PackageInfoCache>();
        services.AddSingleton<IFileDownloader, HttpFileDownloader>();
        services.AddSingleton<ISignatureVerifier, AuthenticodeVerifier>();
        services.AddSingleton<InstallerSizeProbe>();
        services.AddSingleton<IPackageVerifier, PackageVerifier>();
        services.AddSingleton<IUpdateChecker, GitHubUpdateChecker>();
        services.AddSingleton<InstalledStateVerifier>();
        services.AddSingleton<IInstallStrategy, WingetInstallStrategy>();
        services.AddSingleton<IInstallStrategy, DirectInstallStrategy>();
        services.AddSingleton(sp =>
        {
            var verifier = sp.GetRequiredService<InstalledStateVerifier>();
            return new InstallSessionRunner(
                sp.GetServices<IInstallStrategy>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<InstallSessionRunner>>(),
                verifier.VerifyAsync);
        });

        // UI services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<ShellService>();
        services.AddSingleton<FileDialogService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<CatalogState>();
        services.AddSingleton<InstallSessionController>();

        // View models
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ProgramsViewModel>();
        services.AddSingleton<ProgramDetailsViewModel>();
        services.AddSingleton<SelectionViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<ProgressViewModel>();
        services.AddSingleton<ResultsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Arayüzde işlenmeyen hata");
        e.Handled = true;
        var localizer = _services?.GetService<ILocalizer>() ?? new JsonLocalizer();
        var paths = _services?.GetService<IAppPaths>();

        // A failure that repeats on every layout pass would otherwise show dialogs endlessly.
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10))
            _recentErrors.Dequeue();
        if (_recentErrors.Count >= 3)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            MessageBox.Show(localizer.Format("App.FatalError", paths?.LogDirectory ?? string.Empty), "DeployNest", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MessageBox.Show(
            localizer.Format("App.UnexpectedError", paths?.LogDirectory ?? string.Empty),
            "DeployNest",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private readonly Queue<DateTime> _recentErrors = new();
}
