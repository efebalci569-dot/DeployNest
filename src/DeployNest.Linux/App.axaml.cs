using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeployNest.Core.Settings;
using DeployNest.Linux.Services;
using DeployNest.Linux.ViewModels;
using DeployNest.Linux.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeployNest.Linux;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _services = AppServices.Build();
            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogInformation("DeployNest {Version} (Linux) başlatıldı", AppInfo.VersionText);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                logger.LogCritical(args.ExceptionObject as Exception, "İşlenmeyen hata");

            SettingsViewModel.ApplyTheme(_services.GetRequiredService<ISettingsStore>().Load().Theme);

            var state = _services.GetRequiredService<LinuxCatalogState>();
            state.Initialize();

            var main = _services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.Exit += (_, _) =>
            {
                logger.LogInformation("DeployNest kapatıldı");
                _services.Dispose();
            };

            _ = state.DetectSystemAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
