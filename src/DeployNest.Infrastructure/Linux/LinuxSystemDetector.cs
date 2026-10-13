using System.Runtime.InteropServices;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Linux;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Linux;

/// <summary>Finds programs in PATH and the standard system directories.</summary>
public static class ExecutableLocator
{
    private static readonly string[] SystemDirectories = ["/usr/local/sbin", "/usr/local/bin", "/usr/sbin", "/usr/bin", "/sbin", "/bin"];

    /// <summary>
    /// With <paramref name="systemOnly"/>, only root-owned system directories are searched (used by
    /// the privileged helper, which must never run a program from a user-controlled PATH).
    /// </summary>
    public static string? Find(string name, bool systemOnly = false)
    {
        var directories = systemOnly
            ? SystemDirectories
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(SystemDirectories);
        foreach (var directory in directories.Distinct())
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public static IReadOnlyList<string> RequiredTools(LinuxPackageManager manager) => manager switch
    {
        LinuxPackageManager.Apt => ["apt-get", "apt-cache", "dpkg-query"],
        LinuxPackageManager.Dnf => ["dnf", "rpm"],
        _ => ["pacman"],
    };

    public static bool IsAvailable(LinuxPackageManager manager, bool systemOnly = false) =>
        RequiredTools(manager).All(tool => Find(tool, systemOnly) is not null);
}

/// <summary>Reads the distribution, package manager and Flatpak status of this computer.</summary>
public sealed class LinuxSystemDetector(IProcessRunner processes, ILogger<LinuxSystemDetector> logger)
{
    public static readonly IReadOnlyDictionary<string, string> CLocale = new Dictionary<string, string> { ["LC_ALL"] = "C" };

    public static LinuxDistribution ReadDistribution()
    {
        foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                if (File.Exists(path))
                    return LinuxDistribution.Parse(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the next location.
            }
        }
        return LinuxDistribution.Unknown;
    }

    public async Task<LinuxSystemInfo> DetectAsync(CancellationToken cancellationToken)
    {
        var distribution = ReadDistribution();
        var manager = distribution.ChoosePackageManager(m => ExecutableLocator.IsAvailable(m));

        string? flatpakVersion = null;
        if (ExecutableLocator.Find("flatpak") is { } flatpak)
        {
            try
            {
                var result = await processes.RunAsync(new ProcessRequest(flatpak, ["--version"]) { Environment = CLocale }, cancellationToken).ConfigureAwait(false);
                if (result.ExitCode == 0)
                    flatpakVersion = result.StandardOutput.Trim().Replace("Flatpak ", string.Empty, StringComparison.OrdinalIgnoreCase);
            }
            catch (ProcessLaunchException ex)
            {
                logger.LogWarning(ex, "flatpak çalıştırılamadı");
            }
        }

        var info = new LinuxSystemInfo(
            distribution,
            manager,
            flatpakVersion is not null,
            flatpakVersion,
            ExecutableLocator.Find("pkexec") is not null,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        logger.LogInformation("Linux: {Distro}, paket yöneticisi {Manager}, Flatpak {Flatpak}, pkexec {Pkexec}",
            distribution.PrettyName, info.PackageManagerName, flatpakVersion ?? "yok", info.ElevationAvailable);
        return info;
    }
}
