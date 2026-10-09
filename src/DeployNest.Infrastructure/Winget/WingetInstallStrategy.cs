using DeployNest.Core.Abstractions;
using DeployNest.Core.Installation;
using DeployNest.Core.Winget;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Winget;

public sealed class WingetInstallStrategy(
    WingetClient client,
    IWingetLocator locator,
    ILogger<WingetInstallStrategy> logger) : IInstallStrategy
{
    public InstallMethod Method => InstallMethod.Winget;

    public bool SupportsPrefetch => false;

    public async Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var status = await locator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Available)
            return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.WingetMissing, status.Problem));
        if (request.Software.Winget is null || !WingetPackageId.IsValid(request.Software.Winget.PackageId))
            return PreparedInstall.Stop(request, InstallOutcome.Fail(FailureKind.PackageNotFound, "catalog"));
        return PreparedInstall.Ready(request);
    }

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var request = prepared.Request;
        var packageId = request.Software.Winget!.PackageId;
        var interpreter = new WingetProgressInterpreter();
        string? lastLine = null;

        using var tracker = new WingetDownloadTracker(packageId, request.ExpectedSizeBytes, progress);
        progress.Report(new ItemProgress(ItemPhase.Preparing));

        ProcessResult result;
        try
        {
            result = await client.InstallAsync(packageId, request.AllowUpgrade, line =>
            {
                var cleaned = line.Trim();
                if (cleaned.Length > 3)
                    lastLine = cleaned;

                var update = interpreter.OnLine(line);
                if (update is null)
                    return;

                tracker.SetPhase(update.Phase, update.Detail);
                progress.Report(update);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessLaunchException ex)
        {
            logger.LogError(ex, "winget başlatılamadı");
            return InstallOutcome.Fail(FailureKind.WingetMissing, ex.Message);
        }

        var outcome = WingetExitCodes.ToOutcome(result.ExitCode);
        logger.LogInformation("winget install {PackageId}: {Code} → {Status}/{Failure}. Son satır: {LastLine}",
            packageId, WingetExitCodes.Hex(result.ExitCode), outcome.Status, outcome.Failure, lastLine);

        if (outcome.Status == OutcomeStatus.Failed && lastLine is not null)
            outcome = outcome with { TechnicalDetail = $"{outcome.TechnicalDetail} — {lastLine}" };
        return outcome;
    }

    public string? Cleanup(PreparedInstall prepared) => null;
}

/// <summary>
/// winget does not print download progress when its output is redirected. While the download
/// phase is active this tracker measures the installer file that winget writes under
/// %TEMP%\WinGet\&lt;PackageId&gt;.&lt;Version&gt; and reports real byte counts.
/// If that folder cannot be found, no percentage is shown (indeterminate progress).
/// </summary>
internal sealed class WingetDownloadTracker : IDisposable
{
    private readonly string _packageId;
    private readonly long? _expectedSize;
    private readonly IProgress<ItemProgress> _progress;
    private readonly DateTime _startedUtc = DateTime.UtcNow.AddSeconds(-5);
    private readonly Timer _timer;
    private volatile bool _downloading;
    private string? _host;
    private long _lastReported = -1;

    public WingetDownloadTracker(string packageId, long? expectedSize, IProgress<ItemProgress> progress)
    {
        _packageId = packageId;
        _expectedSize = expectedSize is > 0 ? expectedSize : null;
        _progress = progress;
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public static string WingetTempRoot => Path.Combine(Path.GetTempPath(), "WinGet");

    public void SetPhase(ItemPhase phase, string? host)
    {
        _downloading = phase == ItemPhase.Downloading;
        if (host is not null)
            _host = host;
    }

    private void Poll()
    {
        if (!_downloading)
            return;

        var bytes = MeasureDownloadedBytes();
        if (bytes is null || bytes == _lastReported)
            return;

        _lastReported = bytes.Value;
        double? percent = _expectedSize is { } total ? Math.Clamp(bytes.Value * 100.0 / total, 0, 99.5) : null;
        _progress.Report(new ItemProgress(ItemPhase.Downloading, percent, bytes, _expectedSize, _host));
    }

    internal long? MeasureDownloadedBytes()
    {
        try
        {
            if (!Directory.Exists(WingetTempRoot))
                return null;

            long total = 0;
            var found = false;
            foreach (var dir in Directory.EnumerateDirectories(WingetTempRoot, _packageId + ".*"))
            {
                if (Directory.GetLastWriteTimeUtc(dir) < _startedUtc)
                    continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                    found = true;
                }
            }
            return found ? total : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _downloading = false;
        _timer.Dispose();
    }
}
