using System.Reflection;

namespace DeployNest.Linux;

internal static class AppInfo
{
    public static Version Version { get; } = typeof(AppInfo).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string VersionText { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Version.ToString(3);

    public const string RepositoryUrl = "https://github.com/efebalci569-dot/DeployNest";
    public const string ReleasesUrl = RepositoryUrl + "/releases";
    public const string IssuesUrl = RepositoryUrl + "/issues";
}
