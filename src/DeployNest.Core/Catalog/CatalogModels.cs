using System.Text.RegularExpressions;

namespace DeployNest.Core.Catalog;

/// <summary>Validated, immutable software catalog.</summary>
public sealed record SoftwareCatalog(
    int SchemaVersion,
    string? VerifiedOn,
    IReadOnlyList<CatalogCategory> Categories,
    IReadOnlyList<SoftwareEntry> Software,
    IReadOnlyList<CatalogPreset> Presets)
{
    public CatalogCategory? FindCategory(string id) =>
        Categories.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public SoftwareEntry? FindSoftware(string id) =>
        Software.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The part of the catalog that can be installed on <paramref name="platform"/>: programs
    /// without an install method for it are removed, presets restricted to other platforms are
    /// dropped, the remaining presets keep only installable programs (empty presets are dropped)
    /// and categories without programs are hidden. Order values are
    /// kept, so relative ordering does not change.
    /// </summary>
    public SoftwareCatalog ForPlatform(CatalogPlatform platform)
    {
        var software = Software.Where(s => s.Supports(platform)).ToList();
        var ids = software.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var categories = Categories.Where(c => software.Any(s => s.CategoryId == c.Id)).ToList();
        var presets = Presets
            .Where(p => p.IsOfferedOn(platform))
            .Select(p => p with { SoftwareIds = p.SoftwareIds.Where(ids.Contains).ToArray() })
            .Where(p => p.SoftwareIds.Count > 0)
            .ToList();
        return this with { Categories = categories, Software = software, Presets = presets };
    }
}

public enum CatalogPlatform
{
    Windows,
    Linux,
}

public sealed record CatalogCategory(string Id, LocalizedText Name, string IconGlyph, int Order);

public sealed record CatalogPreset(
    string Id,
    LocalizedText Name,
    LocalizedText Description,
    string IconGlyph,
    IReadOnlyList<string> SoftwareIds,
    IReadOnlyList<CatalogPlatform>? Platforms = null)
{
    /// <summary>True when the preset is offered on <paramref name="platform"/> (no restriction means everywhere).</summary>
    public bool IsOfferedOn(CatalogPlatform platform) => Platforms is null || Platforms.Contains(platform);
}

public enum ElevationHint
{
    Unknown,
    Likely,
    Unlikely,
}

public enum InstallerType
{
    Msi,
    Exe,
}

public sealed record IconSpec(string Monogram, string Color);

/// <summary>Installation through the Windows Package Manager community source.</summary>
public sealed record WingetSource(string PackageId, string Publisher);

/// <summary>
/// Installation by downloading the publisher's installer directly. Only used when the catalog
/// pins a SHA-256 hash and/or an expected Authenticode signer.
/// </summary>
public sealed record DirectSource(
    Uri Url,
    IReadOnlyList<string> AllowedHosts,
    InstallerType InstallerType,
    string? Version,
    string? Sha256,
    string? Signer,
    long? SizeBytes,
    IReadOnlyList<string> SilentArguments,
    Uri? SilentArgumentsSource)
{
    /// <summary>
    /// MSI packages use the standard Windows Installer switches. EXE installers run silently only
    /// when the catalog documents publisher-provided switches; otherwise the installer UI is shown.
    /// </summary>
    public bool RunsUnattended => InstallerType == InstallerType.Msi || SilentArguments.Count > 0;
}

/// <summary>Native package managers of the supported Linux distribution families.</summary>
public enum LinuxPackageManager
{
    /// <summary>Debian, Ubuntu, Linux Mint, Pop!_OS, elementary OS…</summary>
    Apt,
    /// <summary>Fedora, RHEL, CentOS Stream, Rocky, AlmaLinux…</summary>
    Dnf,
    /// <summary>Arch Linux, Manjaro, EndeavourOS…</summary>
    Pacman,
}

/// <summary>
/// Installation on Linux: a Flathub application id (installed per user, no administrator
/// rights) and/or package names of the distribution repositories (installed system-wide).
/// </summary>
public sealed record LinuxSource(
    string? FlatpakId,
    IReadOnlyList<string> Apt,
    IReadOnlyList<string> Dnf,
    IReadOnlyList<string> Pacman,
    LocalizedText? Notes)
{
    public IReadOnlyList<string> PackagesFor(LinuxPackageManager manager) => manager switch
    {
        LinuxPackageManager.Apt => Apt,
        LinuxPackageManager.Dnf => Dnf,
        LinuxPackageManager.Pacman => Pacman,
        _ => [],
    };

    public bool HasNativePackages => Apt.Count > 0 || Dnf.Count > 0 || Pacman.Count > 0;
}

public sealed record RegistryRule(Regex DisplayName, Regex? Publisher);

public sealed record DetectionRules(IReadOnlyList<RegistryRule> Registry, IReadOnlyList<string> WingetIds)
{
    public static DetectionRules Empty { get; } = new([], []);
}

public sealed record SoftwareEntry(
    string Id,
    string Name,
    string Publisher,
    string CategoryId,
    LocalizedText Summary,
    LocalizedText Description,
    Uri? Homepage,
    LocalizedText License,
    Uri? LicenseUrl,
    IconSpec Icon,
    IReadOnlyList<string> Tags,
    ElevationHint Elevation,
    LocalizedText? Notes,
    WingetSource? Winget,
    DirectSource? Direct,
    DetectionRules Detection,
    int Order,
    LinuxSource? Linux = null)
{
    /// <summary>True when the program can be installed on Windows (winget or direct download).</summary>
    public bool SupportsWindows => Winget is not null || Direct is not null;

    public bool SupportsLinux => Linux is not null;

    public bool Supports(CatalogPlatform platform) =>
        platform == CatalogPlatform.Windows ? SupportsWindows : SupportsLinux;

    /// <summary>All winget identifiers that indicate this program is installed.</summary>
    public IEnumerable<string> AllWingetIds =>
        Winget is null ? Detection.WingetIds : Detection.WingetIds.Prepend(Winget.PackageId);
}
