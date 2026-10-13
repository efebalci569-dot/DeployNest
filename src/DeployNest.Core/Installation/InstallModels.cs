using DeployNest.Core.Catalog;

namespace DeployNest.Core.Installation;

public enum InstallMethod
{
    Winget,
    Direct,
    /// <summary>Linux: Flathub application installed for the current user.</summary>
    Flatpak,
    /// <summary>Linux: distribution repository packages (apt, dnf or pacman).</summary>
    NativePackage,
}

public enum ItemPhase
{
    Pending,
    Preparing,
    Downloading,
    Verifying,
    Installing,
    Finalizing,
    Succeeded,
    Failed,
    Skipped,
    Canceled,
}

public enum OutcomeStatus
{
    Succeeded,
    Failed,
    Skipped,
    Canceled,
}

/// <summary>Why an item did not succeed. Each value maps to a user-facing explanation.</summary>
public enum FailureKind
{
    None,
    NetworkUnavailable,
    ServerUnavailable,
    DownloadFailed,
    DownloadIncomplete,
    HashMismatch,
    SignatureInvalid,
    UnexpectedRedirect,
    InsecureSource,
    WingetMissing,
    /// <summary>Linux: the program is only available on Flathub and Flatpak is not installed.</summary>
    FlatpakMissing,
    /// <summary>Linux: no Flatpak and no package for this distribution's package manager.</summary>
    UnsupportedDistribution,
    PackageNotFound,
    PublisherMismatch,
    VerificationFailed,
    NoInstallMethod,
    NotApplicable,
    ElevationDenied,
    CancelledInInstaller,
    InstallerFailed,
    AlreadyInstalled,
    DiskFull,
    PackageInUse,
    InstallInProgress,
    BlockedByPolicy,
    RebootRequiredBeforeInstall,
    DependencyFailed,
    AgreementsNotAccepted,
    Canceled,
    Unknown,
}

public sealed record ItemProgress(
    ItemPhase Phase,
    double? Percent = null,
    long? BytesReceived = null,
    long? TotalBytes = null,
    string? Detail = null);

public sealed record InstallOutcome(
    OutcomeStatus Status,
    FailureKind Failure = FailureKind.None,
    string? TechnicalDetail = null,
    int? ExitCode = null,
    bool RebootRequired = false,
    string? Note = null)
{
    public static InstallOutcome Success(int? exitCode = 0, bool rebootRequired = false, string? note = null) =>
        new(OutcomeStatus.Succeeded, FailureKind.None, null, exitCode, rebootRequired, note);

    public static InstallOutcome Fail(FailureKind kind, string? detail = null, int? exitCode = null) =>
        new(OutcomeStatus.Failed, kind, detail, exitCode);

    public static InstallOutcome Skip(FailureKind reason, string? detail = null, int? exitCode = null) =>
        new(OutcomeStatus.Skipped, reason, detail, exitCode);

    public static InstallOutcome Cancel(string? detail = null) =>
        new(OutcomeStatus.Canceled, FailureKind.Canceled, detail);
}

/// <summary>Result of verifying a package before installation (winget metadata and size lookup).</summary>
public sealed record PackageVerification(
    bool Verified,
    FailureKind Problem,
    string? Version,
    string? Publisher,
    Uri? InstallerUrl,
    string? InstallerType,
    long? SizeBytes,
    string? License,
    Uri? LicenseUrl,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Warnings,
    string? Detail)
{
    public static PackageVerification Failed(FailureKind problem, string? detail = null) =>
        new(false, problem, null, null, null, null, null, null, null, [], [], detail);
}

public sealed record InstallRequest(
    SoftwareEntry Software,
    InstallMethod Method,
    bool AllowUpgrade,
    long? ExpectedSizeBytes,
    string DownloadDirectory,
    bool KeepDownloadedFiles);

/// <summary>State handed from the prepare (download/verify) step to the install step.</summary>
public sealed record PreparedInstall(InstallRequest Request, string? LocalFile, InstallOutcome? EarlyOutcome, string? Note = null)
{
    public static PreparedInstall Ready(InstallRequest request, string? localFile = null, string? note = null) =>
        new(request, localFile, null, note);

    public static PreparedInstall Stop(InstallRequest request, InstallOutcome outcome, string? localFile = null) =>
        new(request, localFile, outcome);
}

/// <summary>
/// One installation technique. Prepare downloads and verifies (may run in parallel for several
/// programs); Install runs the installer (always sequential).
/// </summary>
public interface IInstallStrategy
{
    InstallMethod Method { get; }

    /// <summary>True when <see cref="PrepareAsync"/> does real work that may run ahead of the install queue.</summary>
    bool SupportsPrefetch { get; }

    Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken);

    Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken);

    /// <summary>Removes temporary files. Returns a note when cleanup failed (never throws).</summary>
    string? Cleanup(PreparedInstall prepared);
}
