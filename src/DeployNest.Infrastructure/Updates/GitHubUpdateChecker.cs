using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeployNest.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Updates;

/// <summary>
/// Compares the running version with the latest published GitHub release. It only reports the
/// result; nothing is downloaded or installed automatically.
/// </summary>
public sealed class GitHubUpdateChecker(HttpClient http, ILogger<GitHubUpdateChecker> logger) : IUpdateChecker
{
    public const string Repository = "efebalci569-dot/DeployNest";
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repository}/releases/latest");
    public static readonly Uri ReleasesPage = new($"https://github.com/{Repository}/releases");

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(true, false, null, ReleasesPage, "noReleases");
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(false, false, null, null, $"HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var tag = json.RootElement.GetProperty("tag_name").GetString() ?? string.Empty;
            var page = json.RootElement.TryGetProperty("html_url", out var url) && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var u)
                ? u
                : ReleasesPage;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
                return new UpdateCheckResult(false, false, tag, page, "unreadableTag");

            var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
            return new UpdateCheckResult(true, latest > current, latest.ToString(), page, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Güncelleme denetimi başarısız");
            return new UpdateCheckResult(false, false, null, null, ex.GetType().Name);
        }
    }
}
