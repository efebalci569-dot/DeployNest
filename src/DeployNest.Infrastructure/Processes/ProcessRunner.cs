using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DeployNest.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Processes;

/// <summary>
/// Starts processes with an explicit argument list (no shell, no string concatenation) and
/// terminates the whole process tree on cancellation.
/// </summary>
public sealed class ProcessRunner(ILogger<ProcessRunner> logger) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = request.UseShellExecute,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in request.Arguments)
            startInfo.ArgumentList.Add(argument);

        var redirect = !request.UseShellExecute;
        if (redirect)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        if (redirect)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (stdout)
                    stdout.AppendLine(e.Data);
                SafeInvoke(request.OnOutputLine, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;
                lock (stderr)
                    stderr.AppendLine(e.Data);
            };
        }

        try
        {
            if (!process.Start())
                throw new ProcessLaunchException($"'{Path.GetFileName(request.FileName)}' başlatılamadı.", 0);
        }
        catch (Win32Exception ex)
        {
            throw new ProcessLaunchException(ex.Message, ex.NativeErrorCode, ex);
        }

        logger.LogDebug("Süreç başlatıldı: {File} (pid {Pid})", Path.GetFileName(request.FileName), process.Id);

        if (redirect)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        string output, error;
        lock (stdout)
            output = stdout.ToString();
        lock (stderr)
            error = stderr.ToString();
        return new ProcessResult(process.ExitCode, output, error);
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                logger.LogWarning("Süreç ağacı iptal nedeniyle sonlandırıldı (pid {Pid})", process.Id);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException or NotSupportedException)
        {
            // Elevated installers started by a non-elevated parent cannot be terminated from here.
            logger.LogWarning(ex, "Süreç ağacının tamamı sonlandırılamadı (yönetici olarak çalışan alt süreç olabilir)");
        }
    }

    private void SafeInvoke(Action<string>? callback, string line)
    {
        if (callback is null)
            return;
        try
        {
            callback(line);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Süreç çıktısı işlenirken hata");
        }
    }
}
