using System.Net;
using System.Security.Cryptography;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.History;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using DeployNest.Core.Settings;
using DeployNest.Infrastructure.Direct;
using DeployNest.Infrastructure.Downloads;
using DeployNest.Infrastructure.Platform;
using DeployNest.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeployNest.Tests;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deploynest-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch (IOException) { }
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var response = respond(request);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}

public class HttpFileDownloaderTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();

    private static HttpFileDownloader Downloader(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<HttpFileDownloader>.Instance);

    private static HttpResponseMessage Ok(byte[] body, long? declaredLength = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentLength = declaredLength ?? body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    private static DownloadRequest Request(string url, string dir, params string[] hosts) =>
        new(new Uri(url), hosts, dir, "setup.msi", null);

    [Fact]
    public async Task Downloads_and_follows_allowed_redirects()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(r => r.RequestUri!.Host == "a.example" ? Redirect("https://cdn.example/file.msi") : Ok(Payload));

        var result = await Downloader(handler).DownloadAsync(Request("https://a.example/x", temp.Path, "a.example", "cdn.example"), null, CancellationToken.None);

        Assert.Equal(Payload, await File.ReadAllBytesAsync(result.FilePath));
        Assert.Equal("cdn.example", result.FinalUrl.Host);
        Assert.False(File.Exists(result.FilePath + ".part"));
    }

    [Fact]
    public async Task Rejects_redirect_to_unlisted_host()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Redirect("https://evil.example/file.msi"));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(handler).DownloadAsync(Request("https://a.example/x", temp.Path, "a.example"), null, CancellationToken.None));

        Assert.Equal(FailureKind.UnexpectedRedirect, ex.Kind);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData("http://a.example/x")]
    public async Task Rejects_plain_http(string url)
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok(Payload));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(handler).DownloadAsync(Request(url, temp.Path, "a.example"), null, CancellationToken.None));

        Assert.Equal(FailureKind.InsecureSource, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rejects_redirect_downgrade_to_http()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Redirect("http://a.example/file.msi"));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(handler).DownloadAsync(Request("https://a.example/x", temp.Path, "a.example"), null, CancellationToken.None));

        Assert.Equal(FailureKind.InsecureSource, ex.Kind);
    }

    [Fact]
    public async Task Detects_incomplete_download_and_removes_partial_file()
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => Ok(Payload, declaredLength: Payload.Length * 2));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(handler).DownloadAsync(Request("https://a.example/x", temp.Path, "a.example"), null, CancellationToken.None));

        Assert.Equal(FailureKind.DownloadIncomplete, ex.Kind);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, FailureKind.ServerUnavailable)]
    [InlineData(HttpStatusCode.NotFound, FailureKind.DownloadFailed)]
    public async Task Classifies_http_errors(HttpStatusCode status, FailureKind expected)
    {
        using var temp = new TempDirectory();
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        var ex = await Assert.ThrowsAsync<DownloadException>(() =>
            Downloader(handler).DownloadAsync(Request("https://a.example/x", temp.Path, "a.example"), null, CancellationToken.None));

        Assert.Equal(expected, ex.Kind);
    }

    [Theory]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("setup.msi", "setup.msi")]
    [InlineData("..", "installer.bin")]
    public void File_names_cannot_escape_the_download_folder(string input, string expected) =>
        Assert.Equal(expected, HttpFileDownloader.SanitizeFileName(input));
}

public class DirectInstallStrategyTests
{
    private sealed class FileWritingDownloader(byte[] content) : IFileDownloader
    {
        public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(request.DestinationDirectory);
            var path = Path.Combine(request.DestinationDirectory, request.FileName);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            progress?.Report(new DownloadProgress(content.Length, content.Length));
            return new DownloadResult(path, content.Length, request.Url);
        }
    }

    private sealed class FakeSignature(SignatureCheck check) : ISignatureVerifier
    {
        public SignatureCheck Verify(string filePath) => check;
    }

    private sealed class RecordingRunner(Func<ProcessRequest, ProcessResult> run) : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(run(request));
        }
    }

    private sealed class PlentyOfSpace : ISystemProbe
    {
        public long? GetFreeSpace(string path) => long.MaxValue / 2;
        public Task<bool> IsInternetAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private static readonly byte[] Content = "fake installer"u8.ToArray();
    private static readonly string ContentSha = Convert.ToHexStringLower(SHA256.HashData(Content));

    private static SoftwareEntry Software(string? sha, string? signer)
    {
        var direct = new DirectSource(new Uri("https://example.com/app.msi"), ["example.com"], InstallerType.Msi, "1.0", sha, signer, null, [], null);
        return new SoftwareEntry("app", "App", "Pub", "tools", LocalizedText.Of("x"), LocalizedText.Of("x"), null,
            LocalizedText.Of("MIT"), null, new IconSpec("A", "#000000"), [], ElevationHint.Unknown, null, null, direct, DetectionRules.Empty, 0);
    }

    private static (DirectInstallStrategy Strategy, RecordingRunner Runner) Create(SignatureCheck signature, Func<ProcessRequest, ProcessResult>? run = null)
    {
        var runner = new RecordingRunner(run ?? (_ => new ProcessResult(0, "", "")));
        var strategy = new DirectInstallStrategy(new FileWritingDownloader(Content), new FakeSignature(signature), runner, new PlentyOfSpace(), NullLogger<DirectInstallStrategy>.Instance);
        return (strategy, runner);
    }

    private static readonly SignatureCheck GoodSignature = new(SignatureStatus.Valid, "CN=Pub Inc, O=Pub Inc, C=US", null);
    private static readonly IProgress<ItemProgress> NoProgress = new InlineProgress<ItemProgress>(_ => { });

    [Fact]
    public async Task Hash_mismatch_blocks_execution_and_deletes_file()
    {
        using var temp = new TempDirectory();
        var (strategy, runner) = Create(GoodSignature);
        var request = new InstallRequest(Software(new string('a', 64), null), InstallMethod.Direct, false, null, temp.Path, false);

        var prepared = await strategy.PrepareAsync(request, NoProgress, CancellationToken.None);

        Assert.Equal(FailureKind.HashMismatch, prepared.EarlyOutcome!.Failure);
        Assert.Empty(Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Wrong_signer_blocks_execution()
    {
        using var temp = new TempDirectory();
        var (strategy, _) = Create(new SignatureCheck(SignatureStatus.Valid, "CN=Someone Else, O=Someone Else", null));
        var request = new InstallRequest(Software(null, "Pub Inc"), InstallMethod.Direct, false, null, temp.Path, false);

        var prepared = await strategy.PrepareAsync(request, NoProgress, CancellationToken.None);

        Assert.Equal(FailureKind.SignatureInvalid, prepared.EarlyOutcome!.Failure);
    }

    [Fact]
    public async Task Verified_msi_runs_msiexec_with_standard_switches_and_cleans_up()
    {
        using var temp = new TempDirectory();
        var (strategy, runner) = Create(GoodSignature, _ => new ProcessResult(3010, "", ""));
        var request = new InstallRequest(Software(ContentSha, "Pub Inc"), InstallMethod.Direct, false, null, temp.Path, false);

        var prepared = await strategy.PrepareAsync(request, NoProgress, CancellationToken.None);
        var outcome = await strategy.InstallAsync(prepared, NoProgress, CancellationToken.None);
        var cleanupNote = strategy.Cleanup(prepared);

        Assert.Null(prepared.EarlyOutcome);
        Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
        Assert.True(outcome.RebootRequired);
        var call = runner.Requests.Single();
        Assert.EndsWith("msiexec.exe", call.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["/i", prepared.LocalFile!, "/passive", "/norestart"], call.Arguments);
        Assert.Null(cleanupNote);
        Assert.False(File.Exists(prepared.LocalFile));
    }

    [Fact]
    public async Task Declined_uac_prompt_is_reported_as_elevation_denied()
    {
        using var temp = new TempDirectory();
        var (strategy, _) = Create(GoodSignature, _ => throw new ProcessLaunchException("cancelled", 1223));
        var request = new InstallRequest(Software(ContentSha, null), InstallMethod.Direct, false, null, temp.Path, false);

        var prepared = await strategy.PrepareAsync(request, NoProgress, CancellationToken.None);
        var outcome = await strategy.InstallAsync(prepared, NoProgress, CancellationToken.None);

        Assert.Equal(FailureKind.ElevationDenied, outcome.Failure);
    }

    [Theory]
    [InlineData(0, OutcomeStatus.Succeeded, FailureKind.None)]
    [InlineData(1602, OutcomeStatus.Failed, FailureKind.CancelledInInstaller)]
    [InlineData(1618, OutcomeStatus.Failed, FailureKind.InstallInProgress)]
    [InlineData(1638, OutcomeStatus.Skipped, FailureKind.AlreadyInstalled)]
    [InlineData(1603, OutcomeStatus.Failed, FailureKind.InstallerFailed)]
    public void Maps_installer_exit_codes(int code, OutcomeStatus status, FailureKind failure)
    {
        var outcome = DirectInstallStrategy.MapInstallerExitCode(code);

        Assert.Equal(status, outcome.Status);
        Assert.Equal(failure, outcome.Failure);
    }

    [Theory]
    [InlineData("CN=Google LLC, O=Google LLC, L=Mountain View, S=California, C=US", SignatureStatus.Valid, "Google LLC", true)]
    [InlineData("CN=\"Mozilla Corporation\", O=\"Mozilla Corporation\"", SignatureStatus.Valid, "Mozilla Corporation", true)]
    [InlineData("CN=Google LLC Fake, O=Evil Ltd", SignatureStatus.Valid, "Google LLC", false)]
    [InlineData("CN=Google LLC, O=Google LLC", SignatureStatus.Invalid, "Google LLC", false)]
    public void Signer_matching_requires_valid_signature_and_exact_name(string subject, SignatureStatus status, string expected, bool match) =>
        Assert.Equal(match, new SignatureCheck(status, subject, null).IsSignedBy(expected));
}

public class StorageTests
{
    [Fact]
    public void Corrupt_history_is_backed_up_and_app_keeps_working()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.HistoryFile, "{ this is not json");
        var store = new JsonHistoryStore(paths, NullLogger<JsonHistoryStore>.Instance);

        Assert.Empty(store.Load());
        Assert.Single(Directory.GetFiles(temp.Path, "history.json.corrupt-*"));

        store.Append([new HistoryEntry(Guid.NewGuid(), DateTimeOffset.Now, "app", "App", "install", InstallMethod.Winget, OutcomeStatus.Succeeded, FailureKind.None, null, TimeSpan.FromSeconds(3))]);
        Assert.Single(store.Load());

        store.Clear();
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Settings_round_trip_and_are_normalized()
    {
        using var temp = new TempDirectory();
        var store = new JsonSettingsStore(new AppPaths(temp.Path), NullLogger<JsonSettingsStore>.Instance);

        Assert.Equal(new AppSettings().Normalize(), store.Load());

        store.Save(new AppSettings { Theme = ThemePreference.Dark, MaxParallelDownloads = 99, DownloadDirectory = "  " });
        var loaded = store.Load();

        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(AppSettings.MaxParallelDownloadsLimit, loaded.MaxParallelDownloads);
        Assert.Null(loaded.DownloadDirectory);
    }
}

public class LocalizationTests
{
    [Fact]
    public void Every_failure_kind_has_a_turkish_explanation_and_advice()
    {
        var localizer = new JsonLocalizer();

        foreach (var kind in Enum.GetValues<FailureKind>())
        {
            Assert.NotEqual($"Failure.{kind}.Title", localizer[$"Failure.{kind}.Title"]);
            Assert.NotEqual($"Failure.{kind}.Advice", localizer[$"Failure.{kind}.Advice"]);
        }
    }

    [Fact]
    public void Missing_keys_are_visible_instead_of_crashing()
    {
        Assert.Equal("Does.Not.Exist", new JsonLocalizer()["Does.Not.Exist"]);
    }
}
