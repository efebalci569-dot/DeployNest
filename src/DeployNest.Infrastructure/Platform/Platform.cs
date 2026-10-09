using System.Net;
using System.Net.NetworkInformation;
using DeployNest.Core.Abstractions;

namespace DeployNest.Infrastructure.Platform;

/// <summary>Per-user data locations under %LOCALAPPDATA%\DeployNest.</summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths(string? root = null)
    {
        DataDirectory = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeployNest");
        LogDirectory = Path.Combine(DataDirectory, "logs");
        ReportDirectory = Path.Combine(DataDirectory, "reports");
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        HistoryFile = Path.Combine(DataDirectory, "history.json");
        DefaultDownloadDirectory = Path.Combine(DataDirectory, "downloads");
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string ReportDirectory { get; }
    public string CacheDirectory { get; }
    public string SettingsFile { get; }
    public string HistoryFile { get; }
    public string DefaultDownloadDirectory { get; }

    public void EnsureCreated()
    {
        foreach (var dir in new[] { DataDirectory, LogDirectory, ReportDirectory, CacheDirectory })
            Directory.CreateDirectory(dir);
    }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public sealed class SystemProbe : ISystemProbe
{
    private static readonly string[] ProbeHosts = ["cdn.winget.microsoft.com", "download.microsoft.com", "github.com"];

    public long? GetFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A network adapter must be up and at least one well-known host must resolve.</summary>
    public async Task<bool> IsInternetAvailableAsync(CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
            return false;

        foreach (var host in ProbeHosts)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
                if (addresses.Length > 0)
                    return true;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        return false;
    }
}
