using DeployNest.Core.Abstractions;
using DeployNest.Core.History;
using DeployNest.Core.Linux;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using DeployNest.Infrastructure.Catalog;
using DeployNest.Infrastructure.Downloads;
using DeployNest.Infrastructure.Linux;
using DeployNest.Infrastructure.Logging;
using DeployNest.Infrastructure.Platform;
using DeployNest.Infrastructure.Processes;
using DeployNest.Infrastructure.Storage;
using DeployNest.Infrastructure.Updates;
using DeployNest.Linux.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeployNest.Linux.Services;

/// <summary>Composition root of the Linux edition.</summary>
public static class AppServices
{
    public static ServiceProvider Build()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        var loggerProvider = new FileLoggerProvider(paths.LogDirectory);

        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(loggerProvider);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton<ILocalizer>(_ => Strings.Localizer);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton(_ => HttpClientFactory.Create(AppInfo.VersionText));
        services.AddSingleton<IUpdateChecker, GitHubUpdateChecker>();
        services.AddSingleton<CatalogProvider>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IHistoryStore, JsonHistoryStore>();

        services.AddSingleton<LinuxSystemDetector>();
        services.AddSingleton<LinuxSystemInfoProvider>();
        services.AddSingleton<FlatpakClient>();
        services.AddSingleton<NativePackageClient>();
        services.AddSingleton<LinuxInventorySource>();
        services.AddSingleton<LinuxInstallationDetector>();
        services.AddSingleton<PrivilegedSessionProvider>();
        services.AddSingleton<FlatpakInstallStrategy>();
        services.AddSingleton<NativePackageStrategy>();
        services.AddSingleton<IPackageVerifier, LinuxPackageVerifier>();

        services.AddSingleton<LinuxCatalogState>();
        services.AddSingleton<LinuxInstallController>();
        services.AddSingleton<Navigator>();

        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ProgramsViewModel>();
        services.AddSingleton<QueueViewModel>();
        services.AddSingleton<ProgressViewModel>();
        services.AddSingleton<ResultsViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton(sp =>
        {
            var controller = sp.GetRequiredService<LinuxInstallController>();
            CommunityToolkit.Mvvm.ComponentModel.ObservableObject Page(PageKey key) => key switch
            {
                PageKey.Home => Refreshed(sp.GetRequiredService<HomeViewModel>()),
                PageKey.Programs => sp.GetRequiredService<ProgramsViewModel>(),
                PageKey.Install when controller.IsRunning => sp.GetRequiredService<ProgressViewModel>(),
                PageKey.Install when controller.IsCompleted => Refreshed(sp.GetRequiredService<ResultsViewModel>()),
                PageKey.Install => sp.GetRequiredService<QueueViewModel>(),
                PageKey.History => sp.GetRequiredService<HistoryViewModel>(),
                _ => Refreshed(sp.GetRequiredService<SettingsViewModel>()),
            };
            var main = new MainWindowViewModel(sp.GetRequiredService<LinuxCatalogState>(), controller, Page);
            sp.GetRequiredService<Navigator>().NavigateTo = main.Navigate;
            return main;
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static T Refreshed<T>(T page) where T : class
    {
        switch (page)
        {
            case HomeViewModel home: home.RefreshRecent(); break;
            case ResultsViewModel results: results.Refresh(); break;
            case SettingsViewModel settings: settings.RefreshSystem(); break;
        }
        return page;
    }
}
