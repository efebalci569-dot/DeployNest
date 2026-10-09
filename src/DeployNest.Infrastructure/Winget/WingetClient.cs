using System.Text.RegularExpressions;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Detection;
using DeployNest.Core.Winget;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Winget;

/// <summary>
/// Finds winget.exe. It normally lives behind an app execution alias in
/// %LOCALAPPDATA%\Microsoft\WindowsApps, which is not always on PATH for every process.
/// </summary>
public sealed partial class WingetLocator(IProcessRunner runner, ILogger<WingetLocator> logger) : IWingetLocator
{
    public static readonly Version MinimumVersion = new(1, 6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private WingetStatus? _cached;

    public async Task<WingetStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
            return _cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _cached ??= await DetectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WingetStatus> DetectAsync(CancellationToken cancellationToken)
    {
        var path = FindExecutable();
        if (path is null)
        {
            logger.LogWarning("winget bulunamadı");
            return new WingetStatus(false, null, null, "notFound");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await runner.RunAsync(new ProcessRequest(path, ["--version"]), timeout.Token).ConfigureAwait(false);
            var match = VersionRegex().Match(result.StandardOutput);
            if (result.ExitCode != 0 || !match.Success || !Version.TryParse(match.Groups["v"].Value, out var version))
            {
                logger.LogWarning("winget sürümü okunamadı (çıkış kodu {Code})", result.ExitCode);
                return new WingetStatus(false, null, path, "notWorking");
            }

            if (version < MinimumVersion)
            {
                logger.LogWarning("winget sürümü çok eski: {Version}", version);
                return new WingetStatus(false, version.ToString(), path, "tooOld");
            }

            logger.LogInformation("winget bulundu: v{Version}", version);
            return new WingetStatus(true, version.ToString(), path, null);
        }
        catch (Exception ex) when (ex is ProcessLaunchException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogWarning(ex, "winget çalıştırılamadı");
            return new WingetStatus(false, null, path, "notWorking");
        }
    }

    internal static string? FindExecutable()
    {
        var candidates = new List<string>();
        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { candidates.Add(Path.Combine(dir, "winget.exe")); }
            catch (ArgumentException) { }
        }
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"v?(?<v>\d+\.\d+(\.\d+)?(\.\d+)?)")]
    private static partial Regex VersionRegex();
}

/// <summary>Runs winget commands. Only the official "winget" source is ever used.</summary>
public sealed class WingetClient(
    IWingetLocator locator,
    IProcessRunner runner,
    ILogger<WingetClient> logger) : IWingetInventorySource
{
    private static readonly string[] CommonArgs = ["--source", "winget", "--accept-source-agreements", "--disable-interactivity"];

    public async Task<(WingetPackageInfo? Info, int ExitCode)> ShowAsync(string packageId, CancellationToken cancellationToken)
    {
        EnsureValid(packageId);
        var path = await RequirePathAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await runner.RunAsync(
            new ProcessRequest(path, ["show", "--id", packageId, "--exact", .. CommonArgs]),
            timeout.Token).ConfigureAwait(false);

        var info = result.ExitCode == 0 ? WingetShowParser.Parse(result.StandardOutput) : null;
        logger.LogInformation("winget show {PackageId}: kod {Code}, sürüm {Version}", packageId, WingetExitCodes.Hex(result.ExitCode), info?.Version);
        return (info, result.ExitCode);
    }

    public async Task<WingetInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        var status = await locator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Available || status.Path is null)
            return WingetInventory.Unavailable(status.Problem ?? "notFound");

        var file = Path.Combine(Path.GetTempPath(), $"deploynest-export-{Guid.NewGuid():N}.json");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var result = await runner.RunAsync(
                new ProcessRequest(status.Path, ["export", "--output", file, .. CommonArgs]),
                timeout.Token).ConfigureAwait(false);

            if (result.ExitCode != 0 || !File.Exists(file))
            {
                logger.LogWarning("winget export başarısız: {Code}", WingetExitCodes.Hex(result.ExitCode));
                return WingetInventory.Unavailable($"export {WingetExitCodes.Hex(result.ExitCode)}");
            }

            var ids = WingetExportParser.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false));
            logger.LogInformation("winget kurulu paket listesi okundu: {Count} paket", ids.Count);
            return new WingetInventory(true, ids, null);
        }
        catch (Exception ex) when (ex is ProcessLaunchException or System.Text.Json.JsonException or IOException)
        {
            logger.LogWarning(ex, "winget kurulu paket listesi okunamadı");
            return WingetInventory.Unavailable(ex.GetType().Name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("winget export zaman aşımına uğradı");
            return WingetInventory.Unavailable("timeout");
        }
        finally
        {
            TryDelete(file);
        }
    }

    /// <summary>True/false when winget answered clearly, null when the state could not be determined.</summary>
    public async Task<bool?> IsInstalledAsync(string packageId, CancellationToken cancellationToken)
    {
        EnsureValid(packageId);
        var status = await locator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!status.Available || status.Path is null)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var result = await runner.RunAsync(
            new ProcessRequest(status.Path, ["list", "--id", packageId, "--exact", .. CommonArgs]),
            timeout.Token).ConfigureAwait(false);
        return result.ExitCode switch
        {
            0 => true,
            WingetExitCodes.NoApplicationsFound => false,
            _ => null,
        };
    }

    /// <summary>
    /// Installs a package silently with the switches defined in its verified winget manifest.
    /// Package agreements are accepted only after the user confirmed them in the queue screen.
    /// </summary>
    public async Task<ProcessResult> InstallAsync(string packageId, bool allowUpgrade, Action<string> onOutputLine, CancellationToken cancellationToken)
    {
        EnsureValid(packageId);
        var path = await RequirePathAsync(cancellationToken).ConfigureAwait(false);
        List<string> args =
        [
            "install", "--id", packageId, "--exact", .. CommonArgs,
            "--silent", "--accept-package-agreements",
        ];
        if (!allowUpgrade)
            args.Add("--no-upgrade");

        return await runner.RunAsync(new ProcessRequest(path, args) { OnOutputLine = onOutputLine }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> RequirePathAsync(CancellationToken cancellationToken)
    {
        var status = await locator.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status is { Available: true, Path: { } path }
            ? path
            : throw new InvalidOperationException("winget kullanılamıyor.");
    }

    private static void EnsureValid(string packageId)
    {
        if (!WingetPackageId.IsValid(packageId))
            throw new ArgumentException($"Geçersiz winget paket kimliği: '{packageId}'", nameof(packageId));
    }

    private void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Geçici dosya silinemedi");
        }
    }
}
