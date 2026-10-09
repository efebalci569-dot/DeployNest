using System.Net;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;
using DeployNest.Core.Winget;
using DeployNest.Infrastructure.Storage;
using DeployNest.Infrastructure.Winget;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Verification;

/// <summary>
/// Verifies a package right before installation: for winget the identifier must exist in the
/// official source and its publisher must match the catalog; the installer size is read with a
/// metadata-only request to the installer URL from the winget manifest.
/// </summary>
public sealed class PackageVerifier(
    WingetClient winget,
    InstallerSizeProbe sizeProbe,
    PackageInfoCache cache,
    IClock clock,
    ILogger<PackageVerifier> logger) : IPackageVerifier
{
    public async Task<PackageVerification> VerifyAsync(SoftwareEntry software, InstallMethod method, CancellationToken cancellationToken)
    {
        var result = method == InstallMethod.Direct
            ? await VerifyDirectAsync(software, cancellationToken).ConfigureAwait(false)
            : await VerifyWingetAsync(software, cancellationToken).ConfigureAwait(false);

        if (result.Verified)
            cache.Set(software.Id, new CachedPackageInfo(result.Version, result.SizeBytes, clock.Now));
        return result;
    }

    private async Task<PackageVerification> VerifyDirectAsync(SoftwareEntry software, CancellationToken cancellationToken)
    {
        var source = software.Direct!;
        var size = source.SizeBytes ?? await sizeProbe.TryGetSizeAsync(source.Url, cancellationToken).ConfigureAwait(false);
        var warnings = new List<string>();
        if (!source.RunsUnattended)
            warnings.Add("interactive");
        return new PackageVerification(
            true, FailureKind.None, source.Version, software.Publisher, source.Url,
            source.InstallerType == InstallerType.Msi ? "msi" : "exe", size,
            software.License.ToString(), software.LicenseUrl, [], warnings, null);
    }

    private async Task<PackageVerification> VerifyWingetAsync(SoftwareEntry software, CancellationToken cancellationToken)
    {
        var expected = software.Winget!;
        (WingetPackageInfo? Info, int ExitCode) show;
        try
        {
            show = await winget.ShowAsync(expected.PackageId, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return PackageVerification.Failed(FailureKind.WingetMissing);
        }
        catch (ProcessLaunchException ex)
        {
            return PackageVerification.Failed(FailureKind.WingetMissing, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PackageVerification.Failed(FailureKind.VerificationFailed, "timeout");
        }

        if (show.ExitCode is WingetExitCodes.NoApplicationsFound or WingetExitCodes.NoManifestFound)
            return PackageVerification.Failed(FailureKind.PackageNotFound, WingetExitCodes.Hex(show.ExitCode));
        if (show.ExitCode != 0)
        {
            var outcome = WingetExitCodes.ToOutcome(show.ExitCode);
            var kind = outcome.Failure is FailureKind.NetworkUnavailable or FailureKind.DownloadFailed
                ? FailureKind.NetworkUnavailable
                : FailureKind.VerificationFailed;
            return PackageVerification.Failed(kind, WingetExitCodes.Hex(show.ExitCode));
        }

        var info = show.Info;
        if (info is null)
            return PackageVerification.Failed(FailureKind.VerificationFailed, "unreadable");
        if (!string.Equals(info.Id, expected.PackageId, StringComparison.OrdinalIgnoreCase))
            return PackageVerification.Failed(FailureKind.PackageNotFound, info.Id);

        var warnings = new List<string>();
        if (info.Publisher is null)
        {
            warnings.Add("publisherUnreadable");
        }
        else if (!PublisherMatches(info.Publisher, expected.Publisher))
        {
            logger.LogWarning("Yayıncı uyuşmazlığı {Id}: beklenen '{Expected}', winget '{Actual}'", expected.PackageId, expected.Publisher, info.Publisher);
            return PackageVerification.Failed(FailureKind.PublisherMismatch, info.Publisher);
        }

        Uri? installerUrl = null;
        if (info.InstallerUrl is not null && Uri.TryCreate(info.InstallerUrl, UriKind.Absolute, out var parsed))
        {
            installerUrl = parsed;
            if (parsed.Scheme != Uri.UriSchemeHttps)
                warnings.Add("installerNotHttps");
        }

        var size = installerUrl is { Scheme: "https" }
            ? await sizeProbe.TryGetSizeAsync(installerUrl, cancellationToken).ConfigureAwait(false)
            : null;

        Uri.TryCreate(info.LicenseUrl, UriKind.Absolute, out var licenseUrl);
        return new PackageVerification(
            true, FailureKind.None, info.Version, info.Publisher ?? expected.Publisher, installerUrl, info.InstallerType, size,
            info.License, licenseUrl ?? software.LicenseUrl, info.Agreements,
            [.. warnings, .. info.Dependencies.Select(d => "dependency:" + d)], null);
    }

    internal static bool PublisherMatches(string actual, string expected) =>
        string.Equals(Simplify(actual), Simplify(expected), StringComparison.OrdinalIgnoreCase);

    private static string Simplify(string value) =>
        new(value.Where(char.IsLetterOrDigit).ToArray());
}

/// <summary>
/// Reads an installer's size without downloading it (HEAD, or a 1-byte ranged GET when HEAD is
/// not supported). Only HTTPS hops are followed. Results are used for display only.
/// </summary>
public sealed class InstallerSizeProbe(HttpClient http, ILogger<InstallerSizeProbe> logger)
{
    public async Task<long?> TryGetSizeAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await ProbeAsync(url, HttpMethod.Head, timeout.Token).ConfigureAwait(false)
                ?? await ProbeAsync(url, HttpMethod.Get, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "Boyut okunamadı: {Host}", url.Host);
            return null;
        }
    }

    private async Task<long?> ProbeAsync(Uri url, HttpMethod method, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop < 6; hop++)
        {
            if (current.Scheme != Uri.UriSchemeHttps)
                return null;

            using var request = new HttpRequestMessage(method, current);
            if (method == HttpMethod.Get)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } location)
                    return null;
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                return null;

            if (method == HttpMethod.Get)
                return response.Content.Headers.ContentRange?.Length;
            return response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null;
        }
        return null;
    }
}
