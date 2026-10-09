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
}

public sealed record CatalogCategory(string Id, LocalizedText Name, string IconGlyph, int Order);

public sealed record CatalogPreset(
    string Id,
    LocalizedText Name,
    LocalizedText Description,
    string IconGlyph,
    IReadOnlyList<string> SoftwareIds);

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
    int Order)
{
    /// <summary>All winget identifiers that indicate this program is installed.</summary>
    public IEnumerable<string> AllWingetIds =>
        Winget is null ? Detection.WingetIds : Detection.WingetIds.Prepend(Winget.PackageId);
}
