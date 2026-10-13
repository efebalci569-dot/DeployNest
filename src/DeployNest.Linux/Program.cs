using Avalonia;
using DeployNest.Infrastructure.Linux;

namespace DeployNest.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started by pkexec as root: serve install requests over stdin/stdout, no UI.
        if (args.Contains(RootHelper.Argument))
            return RootHelper.RunAsync(Console.In, Console.Out, CancellationToken.None).GetAwaiter().GetResult();

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
