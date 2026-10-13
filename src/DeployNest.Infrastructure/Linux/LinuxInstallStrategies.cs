using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;
using DeployNest.Core.Linux;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Linux;

/// <summary>Installs Flathub applications into the user's Flatpak installation (no root needed).</summary>
public sealed class FlatpakInstallStrategy(FlatpakClient flatpak, ILogger<FlatpakInstallStrategy> logger) : IInstallStrategy
{
    public InstallMethod Method => InstallMethod.Flatpak;

    public bool SupportsPrefetch => false;

    public Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(PreparedInstall.Ready(request));

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var request = prepared.Request;
        if (request.Software.Linux?.FlatpakId is not { } appId)
            return InstallOutcome.Fail(FailureKind.NoInstallMethod);

        progress.Report(new ItemProgress(ItemPhase.Installing));
        ProcessResult result;
        try
        {
            result = await flatpak.InstallAsync(appId, request.AllowUpgrade, line =>
            {
                // Flatpak downloads and installs in one step; its percentage covers both.
                if (LinuxParsers.ParsePercent(line) is { } percent)
                    progress.Report(new ItemProgress(ItemPhase.Installing, percent));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return InstallOutcome.Cancel();
        }
        catch (ProcessLaunchException ex)
        {
            logger.LogWarning(ex, "flatpak başlatılamadı");
            return InstallOutcome.Fail(FailureKind.FlatpakMissing, ex.Message);
        }

        if (result.ExitCode == 0)
            return InstallOutcome.Success();

        var output = result.StandardError + "\n" + result.StandardOutput;
        var kind = LinuxFailureClassifier.Classify(LinuxTool.Flatpak, result.ExitCode, output);
        logger.LogWarning("Flatpak kurulumu başarısız: {App} ({Code}) {Error}", appId, result.ExitCode, FlatpakClient.LastLine(result.StandardError));
        return kind == FailureKind.AlreadyInstalled
            ? InstallOutcome.Skip(kind, FlatpakClient.LastLine(result.StandardError), result.ExitCode)
            : InstallOutcome.Fail(kind, FlatpakClient.LastLine(result.StandardError), result.ExitCode);
    }

    public string? Cleanup(PreparedInstall prepared) => null;
}

/// <summary>
/// Installs distribution packages through the privileged helper. The helper is started on the
/// first package (one password prompt) and reused for the rest of the session.
/// </summary>
public sealed class NativePackageStrategy(PrivilegedSessionProvider elevation, LinuxSystemInfoProvider system, ILogger<NativePackageStrategy> logger) : IInstallStrategy
{
    public InstallMethod Method => InstallMethod.NativePackage;

    public bool SupportsPrefetch => false;

    public Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(PreparedInstall.Ready(request));

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var info = system.Current;
        if (info?.PackageManager is not { } manager || prepared.Request.Software.Linux is not { } linux)
            return InstallOutcome.Fail(FailureKind.UnsupportedDistribution);
        var packages = linux.PackagesFor(manager);
        if (packages.Count == 0)
            return InstallOutcome.Fail(FailureKind.UnsupportedDistribution);

        progress.Report(new ItemProgress(ItemPhase.Preparing, Detail: "pkexec"));
        var (status, session, detail) = await elevation.GetAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            var kind = status == ElevationStatus.Dismissed ? FailureKind.CancelledInInstaller : FailureKind.ElevationDenied;
            return InstallOutcome.Fail(kind, ManualCommand(manager, packages, detail));
        }

        progress.Report(new ItemProgress(ItemPhase.Installing, Detail: string.Join(' ', packages)));
        ProcessResult result;
        try
        {
            result = await session.InstallAsync(packages, installFlatpak: false, line => logger.LogDebug("{Line}", line), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return InstallOutcome.Cancel();
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Yetkili yardımcı kapandı");
            await elevation.ResetAsync().ConfigureAwait(false);
            return InstallOutcome.Fail(FailureKind.Unknown, ex.Message);
        }

        if (result.ExitCode == 0)
            return InstallOutcome.Success();

        var failure = LinuxFailureClassifier.Classify(LinuxFailureClassifier.ToolFor(manager), result.ExitCode, result.StandardOutput + "\n" + result.StandardError);
        logger.LogWarning("Paket kurulumu başarısız: {Packages} ({Code}): {Last}", string.Join(' ', packages), result.ExitCode, FlatpakClient.LastLine(result.StandardOutput));
        return InstallOutcome.Fail(failure, FlatpakClient.LastLine(result.StandardOutput), result.ExitCode);
    }

    /// <summary>The terminal command a user can run instead when elevation is not possible.</summary>
    public static string ManualCommand(LinuxPackageManager manager, IReadOnlyList<string> packages, string? detail)
    {
        var (tool, arguments, _) = RootHelper.InstallCommand(manager, packages);
        var command = manager == LinuxPackageManager.Apt
            ? $"sudo apt install {string.Join(' ', packages)}"
            : $"sudo {tool} {string.Join(' ', arguments)}";
        return string.IsNullOrWhiteSpace(detail) ? command : $"{command} ({detail})";
    }

    public string? Cleanup(PreparedInstall prepared) => null;
}

/// <summary>Holds the detected Linux system information so services created early can read it later.</summary>
public sealed class LinuxSystemInfoProvider
{
    public LinuxSystemInfo? Current { get; set; }
}

/// <summary>Checks Flathub or the distribution repositories before installation.</summary>
public sealed class LinuxPackageVerifier(FlatpakClient flatpak, NativePackageClient native, LinuxSystemInfoProvider system) : IPackageVerifier
{
    public async Task<PackageVerification> VerifyAsync(SoftwareEntry software, InstallMethod method, CancellationToken cancellationToken)
    {
        if (software.Linux is not { } linux)
            return PackageVerification.Failed(FailureKind.NoInstallMethod);

        LinuxQueryResult result;
        string source;
        try
        {
            switch (method)
            {
                case InstallMethod.Flatpak when linux.FlatpakId is { } appId:
                    result = await flatpak.RemoteInfoAsync(appId, cancellationToken).ConfigureAwait(false);
                    source = $"Flathub: {appId}";
                    break;
                case InstallMethod.NativePackage when system.Current?.PackageManager is { } manager && linux.PackagesFor(manager).Count > 0:
                    var packages = linux.PackagesFor(manager);
                    result = await native.QueryAsync(manager, packages, cancellationToken).ConfigureAwait(false);
                    source = $"{system.Current.PackageManagerName}: {string.Join(' ', packages)}";
                    break;
                default:
                    return PackageVerification.Failed(FailureKind.UnsupportedDistribution);
            }
        }
        catch (ProcessLaunchException ex)
        {
            return PackageVerification.Failed(method == InstallMethod.Flatpak ? FailureKind.FlatpakMissing : FailureKind.UnsupportedDistribution, ex.Message);
        }

        if (result.Info is not { } info)
            return PackageVerification.Failed(result.Problem, result.Detail);

        return new PackageVerification(true, FailureKind.None, info.Version, software.Publisher, null, source,
            info.DownloadBytes, info.License, null, [], [], null);
    }
}
