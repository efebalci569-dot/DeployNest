using System.Security.Cryptography;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Direct;

/// <summary>
/// Fallback method: downloads the publisher's installer from the catalog URL, verifies the pinned
/// SHA-256 and/or Authenticode signer and only then runs it. A file that fails any check is
/// deleted and never executed.
/// </summary>
public sealed class DirectInstallStrategy(
    IFileDownloader downloader,
    ISignatureVerifier signatureVerifier,
    IProcessRunner runner,
    ISystemProbe probe,
    ILogger<DirectInstallStrategy> logger) : IInstallStrategy
{
    private const long DiskSafetyMarginBytes = 200L * 1024 * 1024;

    public InstallMethod Method => InstallMethod.Direct;

    public bool SupportsPrefetch => true;

    public async Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var source = request.Software.Direct
            ?? throw new InvalidOperationException($"'{request.Software.Id}' için doğrudan indirme tanımı yok.");

        var directory = Path.Combine(request.DownloadDirectory, request.Software.Id);
        var expectedSize = source.SizeBytes ?? request.ExpectedSizeBytes;
        if (expectedSize is { } size && probe.GetFreeSpace(request.DownloadDirectory) is { } free && free < size + DiskSafetyMarginBytes)
            return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.DiskFull, $"{free} bayt boş"));

        progress.Report(new ItemProgress(ItemPhase.Downloading, 0, 0, expectedSize, source.Url.Host));
        var downloadProgress = new InlineProgress<DownloadProgress>(p =>
        {
            var total = p.TotalBytes ?? expectedSize;
            double? percent = total is > 0 ? Math.Clamp(p.BytesReceived * 100.0 / total.Value, 0, 100) : null;
            progress.Report(new ItemProgress(ItemPhase.Downloading, percent, p.BytesReceived, total, source.Url.Host));
        });

        var download = await downloader.DownloadAsync(
            new DownloadRequest(source.Url, source.AllowedHosts, directory, FileNameFor(request.Software, source), source.SizeBytes),
            downloadProgress,
            cancellationToken).ConfigureAwait(false);

        progress.Report(new ItemProgress(ItemPhase.Verifying));

        if (source.Sha256 is not null)
        {
            var actual = await ComputeSha256Async(download.FilePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SHA-256 uyuşmazlığı: {Id} beklenen {Expected}, hesaplanan {Actual}", request.Software.Id, source.Sha256, actual);
                DeleteQuietly(download.FilePath);
                return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.HashMismatch, $"SHA-256 {actual[..12]}…"));
            }
        }

        var signature = signatureVerifier.Verify(download.FilePath);
        string? note = null;
        if (source.Signer is not null)
        {
            if (!signature.IsSignedBy(source.Signer))
            {
                DeleteQuietly(download.FilePath);
                return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.SignatureInvalid,
                    $"{signature.Status}: {signature.SignerSubject ?? "-"} (beklenen: {source.Signer})"));
            }
        }
        else if (signature.Status == SignatureStatus.Invalid)
        {
            DeleteQuietly(download.FilePath);
            return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.SignatureInvalid, signature.Detail));
        }
        else if (signature.Status == SignatureStatus.NotSigned)
        {
            note = "Kurulum dosyası dijital olarak imzalı değil; katalogdaki SHA-256 değeriyle doğrulandı.";
        }

        return PreparedInstall.Ready(request, download.FilePath, note);
    }

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var source = prepared.Request.Software.Direct!;
        var file = prepared.LocalFile ?? throw new InvalidOperationException("İndirilmiş dosya yok.");
        progress.Report(new ItemProgress(ItemPhase.Installing));

        var request = source.InstallerType == InstallerType.Msi
            ? new ProcessRequest(MsiExecPath, ["/i", file, "/passive", "/norestart"]) { UseShellExecute = true }
            : new ProcessRequest(file, source.SilentArguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(file) };

        try
        {
            var result = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("{Id} kurulum programı çıkış kodu {Code}", prepared.Request.Software.Id, result.ExitCode);
            return MapInstallerExitCode(result.ExitCode);
        }
        catch (ProcessLaunchException ex) when (ex.NativeErrorCode == 1223)
        {
            return InstallOutcome.Fail(FailureKind.ElevationDenied, "UAC");
        }
        catch (ProcessLaunchException ex)
        {
            return InstallOutcome.Fail(FailureKind.InstallerFailed, ex.Message);
        }
    }

    public string? Cleanup(PreparedInstall prepared)
    {
        if (prepared.Request.KeepDownloadedFiles || prepared.LocalFile is null || !File.Exists(prepared.LocalFile))
            return null;

        try
        {
            File.Delete(prepared.LocalFile);
            var directory = Path.GetDirectoryName(prepared.LocalFile);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "İndirilen kurulum dosyası silinemedi");
            return $"İndirilen kurulum dosyası silinemedi, elle silebilirsin: {prepared.LocalFile}";
        }
    }

    /// <summary>Standard Windows Installer and common setup exit codes.</summary>
    internal static InstallOutcome MapInstallerExitCode(int code) => code switch
    {
        0 => InstallOutcome.Success(code),
        3010 or 1641 => InstallOutcome.Success(code, rebootRequired: true),
        1602 or 1223 => InstallOutcome.Fail(FailureKind.CancelledInInstaller, code.ToString(), code),
        1618 => InstallOutcome.Fail(FailureKind.InstallInProgress, code.ToString(), code),
        1625 or 1643 => InstallOutcome.Fail(FailureKind.BlockedByPolicy, code.ToString(), code),
        1633 => InstallOutcome.Fail(FailureKind.NotApplicable, code.ToString(), code),
        1638 => InstallOutcome.Skip(FailureKind.AlreadyInstalled, code.ToString(), code),
        112 => InstallOutcome.Fail(FailureKind.DiskFull, code.ToString(), code),
        _ => InstallOutcome.Fail(FailureKind.InstallerFailed, code.ToString(), code),
    };

    private static string MsiExecPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    internal static string FileNameFor(SoftwareEntry software, DirectSource source)
    {
        var extension = source.InstallerType == InstallerType.Msi ? ".msi" : ".exe";
        var name = Path.GetFileName(Uri.UnescapeDataString(source.Url.AbsolutePath));
        return !string.IsNullOrWhiteSpace(name) && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : software.Id + "-setup" + extension;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private void DeleteQuietly(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Doğrulanamayan dosya silinemedi: {File}", file);
        }
    }
}
