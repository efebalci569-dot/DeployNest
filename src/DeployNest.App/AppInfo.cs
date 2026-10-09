using System.Reflection;

namespace DeployNest.App;

public static class AppInfo
{
    public static Version AssemblyVersion { get; } =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Semantic version from the build (e.g. "0.1.0").</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];

    public static readonly Uri RepositoryUrl = new("https://github.com/efebalci569-dot/DeployNest");
    public static readonly Uri ReleasesUrl = new("https://github.com/efebalci569-dot/DeployNest/releases");
    public static readonly Uri IssuesUrl = new("https://github.com/efebalci569-dot/DeployNest/issues");
    public static readonly Uri WingetDocsUrl = new("https://learn.microsoft.com/windows/package-manager/winget/");
    public static readonly Uri AppInstallerStoreUrl = new("ms-windows-store://pdp/?productid=9NBLGGH4NNS1");
}
