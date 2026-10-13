using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;
using DeployNest.Core.Linux;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Linux;

/// <summary>Result of a package query: metadata, or why it could not be read.</summary>
public sealed record LinuxQueryResult(LinuxPackageInfo? Info, FailureKind Problem, string? Detail)
{
    public bool Found => Info is not null;
}

/// <summary>
/// Flatpak operations. Everything runs in the user's own installation (<c>--user</c>), so no
/// administrator password is needed; Flathub is added to that installation when missing.
/// </summary>
public sealed class FlatpakClient(IProcessRunner processes, ILogger<FlatpakClient> logger)
{
    public const string Remote = "flathub";
    public const string RemoteUrl = "https://dl.flathub.org/repo/flathub.flatpakrepo";

    private readonly SemaphoreSlim _remoteGate = new(1, 1);
    private bool _remoteReady;

    public static string? Executable => ExecutableLocator.Find("flatpak");

    /// <summary>Adds Flathub to the user installation once per run (<c>remote-add --if-not-exists</c>).</summary>
    public async Task<ProcessResult?> EnsureRemoteAsync(CancellationToken cancellationToken)
    {
        if (_remoteReady)
            return null;
        await _remoteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_remoteReady)
                return null;
            var result = await RunAsync(["remote-add", "--user", "--if-not-exists", Remote, RemoteUrl], null, cancellationToken).ConfigureAwait(false);
            _remoteReady = result.ExitCode == 0;
            if (!_remoteReady)
                logger.LogWarning("Flathub eklenemedi ({Code}): {Error}", result.ExitCode, result.StandardError.Trim());
            return result;
        }
        finally
        {
            _remoteGate.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, string?>> ListInstalledAppsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["list", "--app", "--columns=application,version"], null, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("flatpak list: " + result.StandardError.Trim());
        return LinuxParsers.ParseFlatpakList(result.StandardOutput);
    }

    public async Task<LinuxQueryResult> RemoteInfoAsync(string appId, CancellationToken cancellationToken)
    {
        var remote = await EnsureRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (remote is { ExitCode: not 0 })
            return Failed(remote);

        var result = await RunAsync(["remote-info", "--user", Remote, appId], null, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? new LinuxQueryResult(LinuxParsers.ParseFlatpakRemoteInfo(result.StandardOutput), FailureKind.None, null)
            : Failed(result);
    }

    /// <summary>Installs (or with <paramref name="update"/>, updates) an application from Flathub.</summary>
    public async Task<ProcessResult> InstallAsync(string appId, bool update, Action<string> onOutput, CancellationToken cancellationToken)
    {
        var remote = await EnsureRemoteAsync(cancellationToken).ConfigureAwait(false);
        if (remote is { ExitCode: not 0 })
            return remote;

        string[] arguments = update
            ? ["update", "--user", "--noninteractive", "-y", appId]
            : ["install", "--user", "--noninteractive", "-y", Remote, appId];
        return await RunAsync(arguments, onOutput, cancellationToken).ConfigureAwait(false);
    }

    private static LinuxQueryResult Failed(ProcessResult result)
    {
        var output = result.StandardError + "\n" + result.StandardOutput;
        var kind = LinuxFailureClassifier.Classify(LinuxTool.Flatpak, result.ExitCode, output);
        if (kind == FailureKind.InstallerFailed)
            kind = FailureKind.VerificationFailed;
        return new LinuxQueryResult(null, kind, LastLine(result.StandardError));
    }

    public static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        var executable = Executable ?? throw new ProcessLaunchException("flatpak bulunamadı.", 2);
        return processes.RunAsync(new ProcessRequest(executable, arguments)
        {
            Environment = LinuxSystemDetector.CLocale,
            OnOutputLine = onOutput,
        }, cancellationToken);
    }
}

/// <summary>Read-only queries against the distribution package manager (no root needed).</summary>
public sealed class NativePackageClient(IProcessRunner processes, ILogger<NativePackageClient> logger)
{
    public async Task<IReadOnlyDictionary<string, string?>> ListInstalledAsync(LinuxPackageManager manager, CancellationToken cancellationToken)
    {
        var (tool, arguments) = manager switch
        {
            LinuxPackageManager.Apt => ("dpkg-query", new[] { "-W", "-f=${Package}\t${Version}\t${db:Status-Abbrev}\n" }),
            LinuxPackageManager.Dnf => ("rpm", new[] { "-qa", "--qf", "%{NAME}\t%{VERSION}\n" }),
            _ => ("pacman", new[] { "-Q" }),
        };
        var result = await RunAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{tool}: {result.StandardError.Trim()}");
        return manager switch
        {
            LinuxPackageManager.Apt => LinuxParsers.ParseDpkgQuery(result.StandardOutput),
            LinuxPackageManager.Dnf => LinuxParsers.ParseRpmQuery(result.StandardOutput),
            _ => LinuxParsers.ParsePacmanQuery(result.StandardOutput),
        };
    }

    /// <summary>Checks that every package exists in the configured repositories; size is summed.</summary>
    public async Task<LinuxQueryResult> QueryAsync(LinuxPackageManager manager, IReadOnlyList<string> packages, CancellationToken cancellationToken)
    {
        string? version = null;
        long? total = 0;
        foreach (var package in packages)
        {
            var (tool, arguments) = manager switch
            {
                LinuxPackageManager.Apt => ("apt-cache", new[] { "show", "--no-all-versions", package }),
                LinuxPackageManager.Dnf => ("dnf", new[] { "info", "--quiet", package }),
                _ => ("pacman", new[] { "-Si", package }),
            };
            var result = await RunAsync(tool, arguments, cancellationToken).ConfigureAwait(false);
            var info = result.ExitCode != 0 ? null : manager switch
            {
                LinuxPackageManager.Apt => LinuxParsers.ParseAptCacheShow(result.StandardOutput),
                LinuxPackageManager.Dnf => LinuxParsers.ParseDnfInfo(result.StandardOutput),
                _ => LinuxParsers.ParsePacmanInfo(result.StandardOutput),
            };
            if (info is null)
            {
                logger.LogInformation("Paket depoda bulunamadı: {Package} ({Manager}, kod {Code})", package, manager, result.ExitCode);
                var kind = LinuxFailureClassifier.Classify(LinuxFailureClassifier.ToolFor(manager), result.ExitCode == 0 ? 1 : result.ExitCode, result.StandardError + "\n" + result.StandardOutput);
                if (kind is FailureKind.InstallerFailed or FailureKind.None)
                    kind = FailureKind.PackageNotFound;
                return new LinuxQueryResult(null, kind, package);
            }
            version ??= info.Version;
            total = info.DownloadBytes is { } bytes && total is { } sum ? sum + bytes : null;
        }
        return new LinuxQueryResult(new LinuxPackageInfo(version, total, null), FailureKind.None, null);
    }

    private Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var executable = ExecutableLocator.Find(tool) ?? throw new ProcessLaunchException($"{tool} bulunamadı.", 2);
        return processes.RunAsync(new ProcessRequest(executable, arguments) { Environment = LinuxSystemDetector.CLocale }, cancellationToken);
    }
}

/// <summary>Reads the Linux inventory (Flatpak applications and distribution packages).</summary>
public sealed class LinuxInventorySource(FlatpakClient flatpak, NativePackageClient native, ILogger<LinuxInventorySource> logger)
{
    public async Task<LinuxInventory> ReadAsync(LinuxSystemInfo system, CancellationToken cancellationToken)
    {
        // Without Flatpak nothing can be installed through it, so its inventory is known to be empty.
        var flatpakTask = system.FlatpakAvailable
            ? TryAsync("flatpak", () => flatpak.ListInstalledAppsAsync(cancellationToken))
            : Task.FromResult<IReadOnlyDictionary<string, string?>?>(new Dictionary<string, string?>());
        var nativeTask = system.PackageManager is { } manager ? TryAsync(manager.ToString(), () => native.ListInstalledAsync(manager, cancellationToken)) : Task.FromResult<IReadOnlyDictionary<string, string?>?>(null);
        return new LinuxInventory(await flatpakTask.ConfigureAwait(false), await nativeTask.ConfigureAwait(false), system.PackageManager);
    }

    private async Task<IReadOnlyDictionary<string, string?>?> TryAsync(string source, Func<Task<IReadOnlyDictionary<string, string?>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ProcessLaunchException or IOException)
        {
            logger.LogWarning(ex, "Kurulu paket listesi okunamadı ({Source})", source);
            return null;
        }
    }
}
