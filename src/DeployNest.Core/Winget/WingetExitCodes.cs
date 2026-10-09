using DeployNest.Core.Installation;

namespace DeployNest.Core.Winget;

/// <summary>
/// Maps winget return codes to outcomes. Values come from the official table:
/// https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
/// </summary>
public static class WingetExitCodes
{
    public const int Success = 0;
    public const int CtrlSignalReceived = unchecked((int)0x8A150005);
    public const int ShellExecInstallFailed = unchecked((int)0x8A150006);
    public const int DownloadFailed = unchecked((int)0x8A150008);
    public const int NoApplicableInstaller = unchecked((int)0x8A150010);
    public const int InstallerHashMismatch = unchecked((int)0x8A150011);
    public const int NoApplicationsFound = unchecked((int)0x8A150014);
    public const int MultipleApplicationsFound = unchecked((int)0x8A150016);
    public const int NoManifestFound = unchecked((int)0x8A150017);
    public const int CommandRequiresAdmin = unchecked((int)0x8A150019);
    public const int MsStoreBlockedByPolicy = unchecked((int)0x8A15001B);
    public const int MsStoreAppBlockedByPolicy = unchecked((int)0x8A15001C);
    public const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    public const int InstallerSecurityCheckFailed = unchecked((int)0x8A15002D);
    public const int DownloadSizeMismatch = unchecked((int)0x8A15002E);
    public const int BlockedByPolicy = unchecked((int)0x8A15003A);
    public const int PackageAgreementsNotAccepted = unchecked((int)0x8A150041);
    public const int SourceAgreementsNotAccepted = unchecked((int)0x8A150046);
    public const int MsiInstallFailed = unchecked((int)0x8A150049);
    public const int DependenciesValidationFailed = unchecked((int)0x8A15004C);
    public const int UpgradeVersionNotNewer = unchecked((int)0x8A15004F);
    public const int ArchiveScanFailed = unchecked((int)0x8A150060);
    public const int PackageAlreadyInstalled = unchecked((int)0x8A150061);
    public const int AppTerminationReceived = unchecked((int)0x8A15006A);
    public const int DownloadDependencies = unchecked((int)0x8A15006B);
    public const int ServiceUnavailable = unchecked((int)0x8A15006D);
    public const int InstallerZeroByteFile = unchecked((int)0x8A150086);

    public const int InstallPackageInUse = unchecked((int)0x8A150101);
    public const int InstallInstallInProgress = unchecked((int)0x8A150102);
    public const int InstallFileInUse = unchecked((int)0x8A150103);
    public const int InstallMissingDependency = unchecked((int)0x8A150104);
    public const int InstallDiskFull = unchecked((int)0x8A150105);
    public const int InstallInsufficientMemory = unchecked((int)0x8A150106);
    public const int InstallNoNetwork = unchecked((int)0x8A150107);
    public const int InstallContactSupport = unchecked((int)0x8A150108);
    public const int InstallRebootRequiredToFinish = unchecked((int)0x8A150109);
    public const int InstallRebootRequiredForInstall = unchecked((int)0x8A15010A);
    public const int InstallRebootInitiated = unchecked((int)0x8A15010B);
    public const int InstallCancelledByUser = unchecked((int)0x8A15010C);
    public const int InstallAlreadyInstalled = unchecked((int)0x8A15010D);
    public const int InstallDowngrade = unchecked((int)0x8A15010E);
    public const int InstallBlockedByPolicy = unchecked((int)0x8A15010F);
    public const int InstallDependencies = unchecked((int)0x8A150110);
    public const int InstallPackageInUseByApplication = unchecked((int)0x8A150111);
    public const int InstallInvalidParameter = unchecked((int)0x8A150112);
    public const int InstallSystemNotSupported = unchecked((int)0x8A150113);
    public const int InstallUpgradeNotSupported = unchecked((int)0x8A150114);
    public const int InstallCustomError = unchecked((int)0x8A150115);

    /// <summary>Win32 ERROR_CANCELLED, e.g. when the UAC prompt is declined.</summary>
    public const int Win32Cancelled = 1223;

    /// <summary>Windows Installer ERROR_INSTALL_USEREXIT.</summary>
    public const int MsiUserExit = 1602;

    public static InstallOutcome ToOutcome(int exitCode) => exitCode switch
    {
        Success => InstallOutcome.Success(exitCode),
        InstallRebootRequiredToFinish or InstallRebootInitiated => InstallOutcome.Success(exitCode, rebootRequired: true),

        PackageAlreadyInstalled or UpdateNotApplicable or InstallAlreadyInstalled
            or UpgradeVersionNotNewer or InstallDowngrade => InstallOutcome.Skip(FailureKind.AlreadyInstalled, Hex(exitCode), exitCode),

        NoApplicationsFound or NoManifestFound or MultipleApplicationsFound => Fail(FailureKind.PackageNotFound, exitCode),
        NoApplicableInstaller or InstallSystemNotSupported or InstallUpgradeNotSupported => Fail(FailureKind.NotApplicable, exitCode),
        DownloadFailed or DownloadDependencies or InstallerZeroByteFile or ServiceUnavailable => Fail(FailureKind.DownloadFailed, exitCode),
        DownloadSizeMismatch => Fail(FailureKind.DownloadIncomplete, exitCode),
        InstallNoNetwork => Fail(FailureKind.NetworkUnavailable, exitCode),
        InstallerHashMismatch => Fail(FailureKind.HashMismatch, exitCode),
        InstallerSecurityCheckFailed or ArchiveScanFailed => Fail(FailureKind.SignatureInvalid, exitCode),
        InstallDiskFull => Fail(FailureKind.DiskFull, exitCode),
        InstallCancelledByUser or Win32Cancelled or MsiUserExit => Fail(FailureKind.CancelledInInstaller, exitCode),
        CtrlSignalReceived or AppTerminationReceived => InstallOutcome.Cancel(Hex(exitCode)),
        InstallPackageInUse or InstallFileInUse or InstallPackageInUseByApplication => Fail(FailureKind.PackageInUse, exitCode),
        InstallInstallInProgress => Fail(FailureKind.InstallInProgress, exitCode),
        InstallBlockedByPolicy or BlockedByPolicy or MsStoreBlockedByPolicy or MsStoreAppBlockedByPolicy => Fail(FailureKind.BlockedByPolicy, exitCode),
        InstallRebootRequiredForInstall => Fail(FailureKind.RebootRequiredBeforeInstall, exitCode),
        PackageAgreementsNotAccepted or SourceAgreementsNotAccepted => Fail(FailureKind.AgreementsNotAccepted, exitCode),
        CommandRequiresAdmin => Fail(FailureKind.ElevationDenied, exitCode),
        InstallMissingDependency or InstallDependencies or DependenciesValidationFailed => Fail(FailureKind.DependencyFailed, exitCode),
        _ => Fail(FailureKind.InstallerFailed, exitCode),
    };

    public static string Hex(int code) => code is >= 0 and < 0x10000 ? code.ToString() : $"0x{code:X8}";

    private static InstallOutcome Fail(FailureKind kind, int exitCode) =>
        InstallOutcome.Fail(kind, Hex(exitCode), exitCode);
}
