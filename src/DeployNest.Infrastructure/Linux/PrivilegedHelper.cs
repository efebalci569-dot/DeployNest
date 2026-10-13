using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Linux;
using DeployNest.Infrastructure.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeployNest.Infrastructure.Linux;

/// <summary>Message exchanged with the privileged helper (one JSON object per line).</summary>
public sealed record HelperMessage
{
    public int Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string? Op { get; init; }
    public string[]? Packages { get; init; }
    public string? Line { get; init; }
    public int ExitCode { get; init; }
    public string? Manager { get; init; }
    public string? Message { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static HelperMessage? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        try { return JsonSerializer.Deserialize<HelperMessage>(line, Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The root side of DeployNest on Linux, started once per installation session through
/// <c>pkexec DeployNest --root-helper</c> so the user enters the password only once.
/// It accepts nothing but "install these validated package names" (or "install flatpak") and
/// runs the system's package manager from root-owned directories with a fixed argument list;
/// there is no shell and no way to pass options or arbitrary commands.
/// </summary>
public static partial class RootHelper
{
    public const string Argument = "--root-helper";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+._-]{0,99}$")]
    private static partial Regex PackageNameRegex();

    public static bool IsValidPackageName(string? name) => name is not null && PackageNameRegex().IsMatch(name);

    /// <summary>The fixed command for installing <paramref name="packages"/> with <paramref name="manager"/>.</summary>
    public static (string Tool, string[] Arguments, Dictionary<string, string> Environment) InstallCommand(LinuxPackageManager manager, IReadOnlyList<string> packages)
    {
        var environment = new Dictionary<string, string> { ["LC_ALL"] = "C" };
        switch (manager)
        {
            case LinuxPackageManager.Apt:
                environment["DEBIAN_FRONTEND"] = "noninteractive";
                return ("apt-get", ["install", "-y", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold", .. packages], environment);
            case LinuxPackageManager.Dnf:
                return ("dnf", ["install", "-y", .. packages], environment);
            default:
                return ("pacman", ["-S", "--needed", "--noconfirm", .. packages], environment);
        }
    }

    public static async Task<int> RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        async Task Send(HelperMessage message)
        {
            await writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await output.WriteLineAsync(message.Serialize()).ConfigureAwait(false);
                await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        if (!Environment.IsPrivilegedProcess)
        {
            await Send(new HelperMessage { Type = "error", Message = "not-root" }).ConfigureAwait(false);
            return 1;
        }

        var manager = LinuxSystemDetector.ReadDistribution().ChoosePackageManager(m => ExecutableLocator.IsAvailable(m, systemOnly: true));
        await Send(new HelperMessage { Type = "ready", Manager = manager?.ToString().ToLowerInvariant() }).ConfigureAwait(false);

        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);
        var requests = Channel.CreateUnbounded<HelperMessage>();
        CancellationTokenSource? current = null;
        var currentLock = new Lock();

        // Reads requests while a command runs so that "cancel" can stop it.
        var reader = Task.Run(async () =>
        {
            while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                var message = HelperMessage.TryParse(line);
                if (message is null)
                    continue;
                if (message.Op == "cancel")
                {
                    lock (currentLock)
                        current?.Cancel();
                    continue;
                }
                if (message.Op == "exit")
                    break;
                await requests.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
            }
            requests.Writer.TryComplete();
        }, cancellationToken);

        var aptRefreshed = false;
        await foreach (var request in requests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var packages = request.Op switch
            {
                "install" => request.Packages ?? [],
                "install-flatpak" => ["flatpak"],
                _ => [],
            };
            if (manager is null || packages.Length == 0 || packages.Length > 50 || !packages.All(IsValidPackageName))
            {
                await Send(new HelperMessage { Id = request.Id, Type = "done", ExitCode = 2, Message = "rejected" }).ConfigureAwait(false);
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (currentLock)
                current = cts;
            try
            {
                async Task<int> Run(string tool, string[] arguments, Dictionary<string, string> environment)
                {
                    var path = ExecutableLocator.Find(tool, systemOnly: true);
                    if (path is null)
                        return 127;
                    var result = await runner.RunAsync(new ProcessRequest(path, arguments)
                    {
                        Environment = environment,
                        OnOutputLine = line => Send(new HelperMessage { Id = request.Id, Type = "output", Line = line }).GetAwaiter().GetResult(),
                    }, cts.Token).ConfigureAwait(false);
                    foreach (var line in result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        await Send(new HelperMessage { Id = request.Id, Type = "output", Line = line.TrimEnd('\r') }).ConfigureAwait(false);
                    return result.ExitCode;
                }

                if (manager == LinuxPackageManager.Apt && !aptRefreshed)
                {
                    aptRefreshed = true;
                    await Run("apt-get", ["update"], new() { ["LC_ALL"] = "C", ["DEBIAN_FRONTEND"] = "noninteractive" }).ConfigureAwait(false);
                }

                var (tool, arguments, environment) = InstallCommand(manager.Value, packages);
                var exitCode = await Run(tool, arguments, environment).ConfigureAwait(false);
                await Send(new HelperMessage { Id = request.Id, Type = "done", ExitCode = exitCode }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await Send(new HelperMessage { Id = request.Id, Type = "done", ExitCode = 130, Message = "canceled" }).ConfigureAwait(false);
            }
            catch (ProcessLaunchException ex)
            {
                await Send(new HelperMessage { Id = request.Id, Type = "done", ExitCode = 127, Message = ex.Message }).ConfigureAwait(false);
            }
            finally
            {
                lock (currentLock)
                    current = null;
            }
        }

        await reader.ConfigureAwait(false);
        return 0;
    }
}

public enum ElevationStatus
{
    Ready,
    /// <summary>The password dialog was closed.</summary>
    Dismissed,
    /// <summary>Wrong password, no polkit agent or not allowed by policy.</summary>
    NotAuthorized,
    /// <summary>pkexec is not installed.</summary>
    Unavailable,
    Failed,
}

/// <summary>
/// The user side of the privileged helper: starts it once through pkexec (one password prompt)
/// and sends install requests for the rest of the session.
/// </summary>
public sealed class PrivilegedSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Channel<HelperMessage> _messages = Channel.CreateUnbounded<HelperMessage>();
    private int _nextId;

    private PrivilegedSession(Process process, ILogger logger)
    {
        _process = process;
        _logger = logger;
    }

    public string? Manager { get; private set; }

    /// <summary>The command that runs this application again (single-file apphost or <c>dotnet app.dll</c>).</summary>
    public static IReadOnlyList<string> SelfCommand()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");
        // Only reached in development ("dotnet DeployNest.dll"); published builds are single-file apphosts.
#pragma warning disable IL3000
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.Ordinal) &&
            Assembly.GetEntryAssembly()?.Location is { Length: > 0 } dll)
            return [path, dll, RootHelper.Argument];
#pragma warning restore IL3000
        return [path, RootHelper.Argument];
    }

    public static async Task<(ElevationStatus Status, PrivilegedSession? Session, string? Detail)> StartAsync(ILogger logger, CancellationToken cancellationToken)
    {
        // Already root (e.g. started with sudo): no password prompt is needed.
        var command = SelfCommand();
        string fileName;
        IEnumerable<string> arguments;
        if (Environment.IsPrivilegedProcess)
        {
            fileName = command[0];
            arguments = command.Skip(1);
        }
        else
        {
            fileName = ExecutableLocator.Find("pkexec") ?? string.Empty;
            if (fileName.Length == 0)
                return (ElevationStatus.Unavailable, null, "pkexec");
            arguments = command;
        }

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            return (ElevationStatus.Failed, null, ex.Message);
        }

        var session = new PrivilegedSession(process, logger);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        _ = session.PumpAsync();

        try
        {
            // The user may take a while to type the password.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            while (await session._messages.Reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
            {
                var message = await session._messages.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (message.Type == "ready")
                {
                    session.Manager = message.Manager;
                    logger.LogInformation("Yetkili yardımcı başlatıldı (paket yöneticisi: {Manager})", message.Manager ?? "yok");
                    return (ElevationStatus.Ready, session, null);
                }
                if (message.Type == "error")
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            return (ElevationStatus.Failed, null, "timeout");
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        string detail;
        lock (stderr)
            detail = stderr.ToString().Trim();
        var status = process.ExitCode switch
        {
            LinuxFailureClassifier.PkexecDismissed => ElevationStatus.Dismissed,
            LinuxFailureClassifier.PkexecNotAuthorized => ElevationStatus.NotAuthorized,
            _ => ElevationStatus.Failed,
        };
        logger.LogWarning("Yetkili yardımcı başlatılamadı ({Code}): {Detail}", process.ExitCode, detail);
        await session.DisposeAsync().ConfigureAwait(false);
        return (status, null, detail.Length > 0 ? detail : $"pkexec {process.ExitCode}");
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (HelperMessage.TryParse(line) is { } message)
                    await _messages.Writer.WriteAsync(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Yetkili yardımcı çıktısı kapandı");
        }
        finally
        {
            _messages.Writer.TryComplete();
        }
    }

    /// <summary>Installs packages (or the flatpak package with <paramref name="installFlatpak"/>); returns the exit code and output.</summary>
    public async Task<ProcessResult> InstallAsync(IReadOnlyList<string> packages, bool installFlatpak, Action<string>? onOutput, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = Interlocked.Increment(ref _nextId);
            var request = new HelperMessage { Id = id, Op = installFlatpak ? "install-flatpak" : "install", Packages = packages.ToArray() };
            await SendAsync(request).ConfigureAwait(false);

            var output = new StringBuilder();
            using var registration = cancellationToken.Register(() => _ = SendAsync(new HelperMessage { Op = "cancel" }));
            while (await _messages.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var message = await _messages.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (message.Id != id)
                    continue;
                if (message.Type == "output" && message.Line is { } line)
                {
                    output.AppendLine(line);
                    onOutput?.Invoke(line);
                }
                else if (message.Type == "done")
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ProcessResult(message.ExitCode, output.ToString(), message.Message ?? string.Empty);
                }
            }
            throw new IOException("Yetkili yardımcı beklenmedik şekilde kapandı.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(HelperMessage message)
    {
        try
        {
            await _process.StandardInput.WriteLineAsync(message.Serialize()).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Yetkili yardımcıya yazılamadı");
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                await SendAsync(new HelperMessage { Op = "exit" }).ConfigureAwait(false);
                _process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { _logger.LogWarning("Yetkili yardımcı 10 sn içinde kapanmadı"); }
            }
        }
        catch (InvalidOperationException)
        {
            // The process never started or was already disposed.
        }
        _process.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Starts the privileged helper on first use and keeps it (or its failure) for the session.</summary>
public sealed class PrivilegedSessionProvider(ILogger<PrivilegedSessionProvider> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PrivilegedSession? _session;
    private (ElevationStatus Status, string? Detail)? _failure;

    public async Task<(ElevationStatus Status, PrivilegedSession? Session, string? Detail)> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
                return (ElevationStatus.Ready, _session, null);
            if (_failure is { } failure)
                return (failure.Status, null, failure.Detail);

            var (status, session, detail) = await PrivilegedSession.StartAsync(logger, cancellationToken).ConfigureAwait(false);
            if (session is not null)
                _session = session;
            else
                _failure = (status, detail);
            return (status, session, detail);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the helper; the next request asks for the password again.</summary>
    public async Task ResetAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not null)
                await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
            _failure = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
