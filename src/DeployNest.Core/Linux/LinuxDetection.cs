using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Localization;

namespace DeployNest.Core.Linux;

/// <summary>
/// Installed applications on Linux: Flatpak application ids (user and system installations) and
/// distribution packages. A null part means that source has not been read or is not available.
/// </summary>
public sealed record LinuxInventory(
    IReadOnlyDictionary<string, string?>? FlatpakApps,
    IReadOnlyDictionary<string, string?>? NativePackages,
    LinuxPackageManager? PackageManager)
{
    public static LinuxInventory Empty { get; } = new(null, null, null);
}

/// <summary>
/// Decides whether a catalog program is installed on Linux. Matches are exact: the Flathub
/// application id, or every distribution package of the entry for this package manager.
/// </summary>
public sealed class LinuxInstallationDetector(ILocalizer localizer)
{
    public DetectionResult Evaluate(SoftwareEntry entry, LinuxInventory inventory)
    {
        if (entry.Linux is not { } linux)
            return new DetectionResult(InstallState.Unverifiable, null, localizer["Linux.Detection.NotForLinux"], null);

        if (linux.FlatpakId is { } flatpakId && inventory.FlatpakApps is { } apps && apps.TryGetValue(flatpakId, out var flatpakVersion))
            return new DetectionResult(InstallState.Installed, flatpakVersion, localizer.Format("Linux.Detection.Flatpak", flatpakId), null);

        var packages = inventory.PackageManager is { } manager ? linux.PackagesFor(manager) : [];
        if (packages.Count > 0 && inventory.NativePackages is { } installed && packages.All(installed.ContainsKey))
        {
            return new DetectionResult(InstallState.Installed, installed[packages[0]],
                localizer.Format("Linux.Detection.Native", string.Join(", ", packages), PackageManagerName(inventory.PackageManager)), null);
        }

        var flatpakChecked = linux.FlatpakId is not null && inventory.FlatpakApps is not null;
        var nativeChecked = packages.Count > 0 && inventory.NativePackages is not null;
        if (flatpakChecked || nativeChecked)
            return new DetectionResult(InstallState.NotInstalled, null, localizer["Linux.Detection.NotFound"], null);

        return new DetectionResult(InstallState.Unverifiable, null, localizer["Linux.Detection.NoSource"], null);
    }

    private static string PackageManagerName(LinuxPackageManager? manager) => manager?.ToString().ToLowerInvariant() ?? "-";
}
