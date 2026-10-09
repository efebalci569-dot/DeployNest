namespace DeployNest.Core.Detection;

public enum InstallState
{
    /// <summary>Detection is still running.</summary>
    Checking,
    Installed,
    NotInstalled,
    /// <summary>No reliable detection source was available for this program.</summary>
    Unverifiable,
}

/// <summary>An entry from the standard Windows "Uninstall" registry keys.</summary>
public sealed record InstalledProgram(
    string DisplayName,
    string? Publisher,
    string? DisplayVersion,
    string? DisplayIcon,
    string Hive);

public interface IInstalledProgramSource
{
    /// <summary>Reads the uninstall registry entries. Throws if the registry cannot be read.</summary>
    Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken);
}

public sealed record WingetInventory(bool Available, IReadOnlySet<string> PackageIds, string? Problem)
{
    public static WingetInventory Unavailable(string problem) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), problem);
}

public interface IWingetInventorySource
{
    /// <summary>Returns the winget identifiers of installed packages (never throws for winget failures).</summary>
    Task<WingetInventory> GetInventoryAsync(CancellationToken cancellationToken);
}

public sealed record DetectionResult(InstallState State, string? Version, string? Evidence, string? IconPath)
{
    public static DetectionResult Checking { get; } = new(InstallState.Checking, null, null, null);
}

/// <summary>Snapshot of every detection source; null means the source has not been read (yet).</summary>
public sealed record DetectionSnapshot(
    IReadOnlyList<InstalledProgram>? Registry,
    bool RegistryFailed,
    WingetInventory? Winget);
