using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;

namespace DeployNest.Core.Abstractions;

/// <summary>
/// A process launch. Arguments are always passed as a list (never concatenated into a shell
/// command line), so values from the catalog cannot inject extra commands.
/// </summary>
public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Required to let an installer show its own UAC prompt. Output cannot be captured then.
    /// </summary>
    public bool UseShellExecute { get; init; }

    public Action<string>? OnOutputLine { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Thrown when a process cannot be started (e.g. file missing, UAC declined).</summary>
public sealed class ProcessLaunchException(string message, int nativeErrorCode, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Win32 error code; 1223 (ERROR_CANCELLED) means the UAC prompt was declined.</summary>
    public int NativeErrorCode { get; } = nativeErrorCode;
}

public interface IProcessRunner
{
    /// <summary>
    /// Runs a process to completion. On cancellation the whole process tree is terminated and
    /// <see cref="OperationCanceledException"/> is thrown.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed record DownloadRequest(
    Uri Url,
    IReadOnlyCollection<string> AllowedHosts,
    string DestinationDirectory,
    string FileName,
    long? ExpectedSizeBytes);

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes);

public sealed record DownloadResult(string FilePath, long Bytes, Uri FinalUrl);

/// <summary>Download failure with a classified reason.</summary>
public sealed class DownloadException(FailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public FailureKind Kind { get; } = kind;
}

public interface IFileDownloader
{
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken);
}

public enum SignatureStatus
{
    Valid,
    NotSigned,
    Invalid,
    Error,
}

public sealed record SignatureCheck(SignatureStatus Status, string? SignerSubject, string? Detail)
{
    /// <summary>True when the signature is valid and the signer's O= or CN= equals <paramref name="expected"/>.</summary>
    public bool IsSignedBy(string expected)
    {
        if (Status != SignatureStatus.Valid || SignerSubject is null)
            return false;
        foreach (var part in SignerSubject.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = part[..separator];
            var value = part[(separator + 1)..].Trim('"');
            if ((key is "O" or "CN") && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public interface ISignatureVerifier
{
    SignatureCheck Verify(string filePath);
}

public interface IClock
{
    DateTimeOffset Now { get; }
}

public interface ISystemProbe
{
    /// <summary>Free bytes on the drive that contains <paramref name="path"/>, or null if unknown.</summary>
    long? GetFreeSpace(string path);

    Task<bool> IsInternetAvailableAsync(CancellationToken cancellationToken);
}

public interface IAppPaths
{
    string DataDirectory { get; }
    string LogDirectory { get; }
    string ReportDirectory { get; }
    string CacheDirectory { get; }
    string SettingsFile { get; }
    string HistoryFile { get; }
    string DefaultDownloadDirectory { get; }
}

/// <summary>Verifies a package before installation and estimates its download size.</summary>
public interface IPackageVerifier
{
    Task<PackageVerification> VerifyAsync(SoftwareEntry software, InstallMethod method, CancellationToken cancellationToken);
}

public sealed record WingetStatus(bool Available, string? Version, string? Path, string? Problem);

public interface IWingetLocator
{
    Task<WingetStatus> GetStatusAsync(CancellationToken cancellationToken);
}

public sealed record UpdateCheckResult(bool Succeeded, bool UpdateAvailable, string? LatestVersion, Uri? ReleaseUrl, string? Message);

public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken);
}
