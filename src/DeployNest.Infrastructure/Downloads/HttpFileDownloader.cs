using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Installation;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Downloads;

/// <summary>Creates the single, shared <see cref="HttpClient"/> used for all downloads.</summary>
public static class HttpClientFactory
{
    public static HttpClient Create(string userAgentVersion)
    {
        var handler = new SocketsHttpHandler
        {
            // Redirects are followed manually so every hop can be checked against the allow list.
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DeployNest", userAgentVersion));
        return client;
    }
}

/// <summary>
/// Downloads installers over HTTPS only. Every redirect target must be HTTPS and its host must be
/// listed in the catalog entry's allow list; anything else is rejected. Data is written to a
/// ".part" file that is renamed only after the full length was received.
/// </summary>
public sealed class HttpFileDownloader(HttpClient http, ILogger<HttpFileDownloader> logger) : IFileDownloader
{
    public const int MaxRedirects = 5;
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(60);

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var allowed = new HashSet<string>(request.AllowedHosts, StringComparer.OrdinalIgnoreCase);
        EnsureAllowed(request.Url, allowed);

        var safeName = SanitizeFileName(request.FileName);
        Directory.CreateDirectory(request.DestinationDirectory);
        var target = Path.Combine(request.DestinationDirectory, safeName);
        var partial = target + ".part";

        try
        {
            using var response = await SendFollowingRedirectsAsync(request.Url, allowed, cancellationToken).ConfigureAwait(false);
            var finalUrl = response.RequestMessage?.RequestUri ?? request.Url;
            var contentLength = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = Environment.TickCount64;
                progress?.Report(new DownloadProgress(0, contentLength));

                while (true)
                {
                    using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    readTimeout.CancelAfter(ReadInactivityTimeout);
                    int read;
                    try
                    {
                        read = await source.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new DownloadException(FailureKind.ServerUnavailable, "Sunucu 60 saniye boyunca veri göndermedi.");
                    }

                    if (read == 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    if (Environment.TickCount64 - lastReport > 250)
                    {
                        progress?.Report(new DownloadProgress(received, contentLength));
                        lastReport = Environment.TickCount64;
                    }
                }

                progress?.Report(new DownloadProgress(received, contentLength ?? received));

                if (contentLength is { } expected && received != expected)
                    throw new DownloadException(FailureKind.DownloadIncomplete, $"{received} / {expected} bayt alındı.");
                if (request.ExpectedSizeBytes is { } catalogSize && received != catalogSize)
                    throw new DownloadException(FailureKind.DownloadIncomplete, $"Beklenen boyut {catalogSize} bayt, alınan {received} bayt.");
                if (received == 0)
                    throw new DownloadException(FailureKind.DownloadIncomplete, "Sunucu boş dosya döndürdü.");
            }

            File.Move(partial, target, overwrite: true);
            logger.LogInformation("İndirildi: {Host} → {File} ({Bytes} bayt)", finalUrl.Host, safeName, new FileInfo(target).Length);
            return new DownloadResult(target, new FileInfo(target).Length, finalUrl);
        }
        catch (Exception ex)
        {
            TryDelete(partial);
            if (ex is DownloadException or OperationCanceledException)
                throw;
            throw Classify(ex);
        }
    }

    private async Task<HttpResponseMessage> SendFollowingRedirectsAsync(Uri url, HashSet<string> allowed, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, current), HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadException(FailureKind.ServerUnavailable, "Sunucu zamanında yanıt vermedi.");
            }

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null)
                    throw new DownloadException(FailureKind.UnexpectedRedirect, "Yönlendirme adresi boş.");

                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                EnsureAllowed(next, allowed);
                logger.LogDebug("Yönlendirme: {From} → {To}", current.Host, next.Host);
                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                response.Dispose();
                throw new DownloadException(
                    status >= 500 || status == 429 ? FailureKind.ServerUnavailable : FailureKind.DownloadFailed,
                    $"HTTP {status}");
            }

            return response;
        }

        throw new DownloadException(FailureKind.UnexpectedRedirect, $"{MaxRedirects} yönlendirmeden fazlası reddedildi.");
    }

    internal static void EnsureAllowed(Uri url, HashSet<string> allowed)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new DownloadException(FailureKind.InsecureSource, $"HTTPS olmayan adres reddedildi: {url.Scheme}://{url.Host}");
        if (!allowed.Contains(url.Host))
            throw new DownloadException(FailureKind.UnexpectedRedirect, $"İzin verilmeyen sunucu reddedildi: {url.Host}");
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    internal static string SanitizeFileName(string name)
    {
        var fileName = Path.GetFileName(name);
        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." ? "installer.bin" : fileName;
    }

    private static DownloadException Classify(Exception ex) => ex switch
    {
        IOException io when IsDiskFull(io) => new DownloadException(FailureKind.DiskFull, io.Message, io),
        HttpRequestException { InnerException: SocketException } http => new DownloadException(FailureKind.NetworkUnavailable, http.Message, http),
        HttpRequestException http => new DownloadException(FailureKind.ServerUnavailable, http.Message, http),
        IOException io => new DownloadException(FailureKind.DownloadIncomplete, io.Message, io),
        UnauthorizedAccessException ua => new DownloadException(FailureKind.DownloadFailed, ua.Message, ua),
        _ => new DownloadException(FailureKind.DownloadFailed, ex.Message, ex),
    };

    /// <summary>ERROR_DISK_FULL (112) or ERROR_HANDLE_DISK_FULL (39).</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;

    private void TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Yarım kalan indirme dosyası silinemedi");
        }
    }
}
