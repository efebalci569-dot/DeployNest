using System.Diagnostics;
using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace DeployNest.Core.Installation;

public sealed record InstallRunOptions(int MaxParallelDownloads, string DownloadDirectory, bool KeepDownloadedFiles);

public abstract record SessionEvent(string SoftwareId);

public sealed record ItemProgressEvent(string SoftwareId, ItemProgress Progress) : SessionEvent(SoftwareId);

public sealed record ItemCompletedEvent(string SoftwareId, ItemResult Result) : SessionEvent(SoftwareId);

public sealed record ItemResult(
    SoftwareEntry Software,
    InstallMethod? Method,
    InstallOutcome Outcome,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    bool? PostInstallVerified,
    IReadOnlyList<string> Notes);

public sealed record SessionResult(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<ItemResult> Items,
    bool CancelRequested)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
    public IEnumerable<ItemResult> Succeeded => Items.Where(i => i.Outcome.Status == OutcomeStatus.Succeeded);
    public IEnumerable<ItemResult> Failed => Items.Where(i => i.Outcome.Status == OutcomeStatus.Failed);
    public IEnumerable<ItemResult> Skipped => Items.Where(i => i.Outcome.Status == OutcomeStatus.Skipped);
    public IEnumerable<ItemResult> Canceled => Items.Where(i => i.Outcome.Status == OutcomeStatus.Canceled);
    public bool RebootRequired => Items.Any(i => i.Outcome.RebootRequired);
}

/// <summary>
/// Two-level cancellation. <see cref="RequestStop"/> cancels pending items and downloads but lets
/// a running installer finish (safe). <see cref="RequestAbort"/> also terminates the running
/// installer, which may leave that program partially installed.
/// </summary>
public sealed class SessionCancellation : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _abort = new();

    public CancellationToken StopToken => _stop.Token;
    public CancellationToken AbortToken => _abort.Token;
    public bool IsStopRequested => _stop.IsCancellationRequested;
    public bool IsAbortRequested => _abort.IsCancellationRequested;

    public void RequestStop() => _stop.Cancel();

    public void RequestAbort()
    {
        _stop.Cancel();
        _abort.Cancel();
    }

    public void Dispose()
    {
        _stop.Dispose();
        _abort.Dispose();
    }
}

/// <summary>Checks after a reported success whether the program is really detected as installed.</summary>
public delegate Task<bool?> PostInstallVerifier(SoftwareEntry software, CancellationToken cancellationToken);

/// <summary>
/// Runs an installation plan. Downloads for strategies that support prefetching run in parallel
/// (bounded by <see cref="InstallRunOptions.MaxParallelDownloads"/>); installers always run one
/// at a time because Windows Installer and most setup programs cannot run concurrently.
/// A failing item never stops the remaining items.
/// </summary>
public sealed class InstallSessionRunner
{
    private readonly IReadOnlyDictionary<InstallMethod, IInstallStrategy> _strategies;
    private readonly IClock _clock;
    private readonly ILogger<InstallSessionRunner> _logger;
    private readonly PostInstallVerifier? _postInstallVerifier;
    private int _running;

    public InstallSessionRunner(
        IEnumerable<IInstallStrategy> strategies,
        IClock clock,
        ILogger<InstallSessionRunner> logger,
        PostInstallVerifier? postInstallVerifier = null)
    {
        _strategies = strategies.ToDictionary(s => s.Method);
        _clock = clock;
        _logger = logger;
        _postInstallVerifier = postInstallVerifier;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<SessionResult> RunAsync(
        InstallPlan plan,
        InstallRunOptions options,
        IProgress<SessionEvent>? events,
        SessionCancellation cancellation)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("Bir kurulum oturumu zaten çalışıyor.");

        try
        {
            return await RunCoreAsync(plan, options, events, cancellation).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<SessionResult> RunCoreAsync(
        InstallPlan plan,
        InstallRunOptions options,
        IProgress<SessionEvent>? events,
        SessionCancellation cancellation)
    {
        var duplicates = plan.Items.GroupBy(i => i.Software.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ArgumentException($"Plan aynı programı birden fazla içeriyor: {string.Join(", ", duplicates)}", nameof(plan));

        var sessionId = Guid.NewGuid();
        var startedAt = _clock.Now;
        var results = new List<ItemResult>();
        _logger.LogInformation("Kurulum oturumu {SessionId} başladı: {Count} program", sessionId, plan.Items.Count);

        foreach (var item in plan.Items.Where(i => i.Decision != PlanDecision.Install))
        {
            var outcome = item.Decision == PlanDecision.SkipInstalled
                ? InstallOutcome.Skip(FailureKind.AlreadyInstalled)
                : InstallOutcome.Fail(item.Reason, item.Verification?.Detail);
            var result = new ItemResult(item.Software, item.Method, outcome, _clock.Now, TimeSpan.Zero, null, []);
            results.Add(result);
            events?.Report(new ItemCompletedEvent(item.Software.Id, result));
        }

        var installItems = plan.ToInstall.ToList();
        var maxParallel = Math.Max(1, options.MaxParallelDownloads);
        using var limiter = new SemaphoreSlim(maxParallel, maxParallel);
        using var prefetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation.StopToken, cancellation.AbortToken);
        var prefetches = new Dictionary<string, Task<PreparedInstall>>(StringComparer.Ordinal);

        foreach (var item in installItems)
        {
            var strategy = GetStrategy(item);
            if (!strategy.SupportsPrefetch)
                continue;
            var request = CreateRequest(item, options);
            var progress = ItemProgressReporter(events, item.Software.Id);
            prefetches[item.Software.Id] = PrefetchAsync(strategy, request, limiter, progress, prefetchCts.Token);
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in installItems)
        {
            var id = item.Software.Id;
            consumed.Add(id);

            if (cancellation.IsStopRequested)
            {
                if (prefetches.TryGetValue(id, out var pending))
                    await CleanupPrefetchAsync(GetStrategy(item), pending).ConfigureAwait(false);
                var canceled = new ItemResult(item.Software, item.Method, InstallOutcome.Cancel(), _clock.Now, TimeSpan.Zero, null, []);
                results.Add(canceled);
                events?.Report(new ItemCompletedEvent(id, canceled));
                continue;
            }

            var result = await RunItemAsync(item, options, events, cancellation, prefetches.GetValueOrDefault(id)).ConfigureAwait(false);
            results.Add(result);
            events?.Report(new ItemCompletedEvent(id, result));
        }

        foreach (var (id, task) in prefetches)
        {
            if (consumed.Contains(id))
                continue;
            var item = installItems.First(i => i.Software.Id == id);
            await CleanupPrefetchAsync(GetStrategy(item), task).ConfigureAwait(false);
        }

        var finishedAt = _clock.Now;
        _logger.LogInformation(
            "Kurulum oturumu {SessionId} bitti: {Succeeded} başarılı, {Failed} başarısız, {Skipped} atlandı, {Canceled} iptal",
            sessionId,
            results.Count(r => r.Outcome.Status == OutcomeStatus.Succeeded),
            results.Count(r => r.Outcome.Status == OutcomeStatus.Failed),
            results.Count(r => r.Outcome.Status == OutcomeStatus.Skipped),
            results.Count(r => r.Outcome.Status == OutcomeStatus.Canceled));

        var ordered = plan.Items
            .Select(p => results.First(r => r.Software.Id == p.Software.Id))
            .ToList();
        return new SessionResult(sessionId, startedAt, finishedAt, ordered, cancellation.IsStopRequested);
    }

    private async Task<ItemResult> RunItemAsync(
        PlanItem item,
        InstallRunOptions options,
        IProgress<SessionEvent>? events,
        SessionCancellation cancellation,
        Task<PreparedInstall>? prefetch)
    {
        var strategy = GetStrategy(item);
        var progress = ItemProgressReporter(events, item.Software.Id);
        var request = CreateRequest(item, options);
        var startedAt = _clock.Now;
        var stopwatch = Stopwatch.StartNew();
        var notes = new List<string>();
        PreparedInstall? prepared = null;
        InstallOutcome outcome;

        _logger.LogInformation("{SoftwareId} kuruluyor ({Method})", item.Software.Id, item.Method);
        try
        {
            if (prefetch is null)
                progress.Report(new ItemProgress(ItemPhase.Preparing));

            prepared = prefetch is not null
                ? await prefetch.ConfigureAwait(false)
                : await strategy.PrepareAsync(request, progress, cancellation.AbortToken).ConfigureAwait(false);
            if (prepared.Note is not null)
                notes.Add(prepared.Note);

            outcome = prepared.EarlyOutcome
                ?? await strategy.InstallAsync(prepared, progress, cancellation.AbortToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsStopRequested)
        {
            outcome = InstallOutcome.Cancel(cancellation.IsAbortRequested ? "abort" : "stop");
        }
        catch (DownloadException ex)
        {
            _logger.LogWarning(ex, "{SoftwareId} indirilemedi: {Kind}", item.Software.Id, ex.Kind);
            outcome = InstallOutcome.Fail(ex.Kind, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{SoftwareId} kurulurken beklenmeyen hata", item.Software.Id);
            outcome = InstallOutcome.Fail(FailureKind.Unknown, ex.Message);
        }
        finally
        {
            if (prepared is not null && strategy.Cleanup(prepared) is { } cleanupNote)
                notes.Add(cleanupNote);
        }

        bool? verified = null;
        if (outcome.Status == OutcomeStatus.Succeeded && _postInstallVerifier is not null)
        {
            progress.Report(new ItemProgress(ItemPhase.Finalizing));
            try
            {
                verified = await _postInstallVerifier(item.Software, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{SoftwareId} kurulum sonrası doğrulanamadı", item.Software.Id);
            }
        }

        stopwatch.Stop();
        progress.Report(new ItemProgress(outcome.Status switch
        {
            OutcomeStatus.Succeeded => ItemPhase.Succeeded,
            OutcomeStatus.Skipped => ItemPhase.Skipped,
            OutcomeStatus.Canceled => ItemPhase.Canceled,
            _ => ItemPhase.Failed,
        }));

        _logger.LogInformation(
            "{SoftwareId} sonuç: {Status} {Failure} kod={ExitCode} doğrulandı={Verified} süre={Duration}",
            item.Software.Id, outcome.Status, outcome.Failure, outcome.ExitCode, verified, stopwatch.Elapsed);

        return new ItemResult(item.Software, item.Method, outcome, startedAt, stopwatch.Elapsed, verified, notes);
    }

    private static async Task<PreparedInstall> PrefetchAsync(
        IInstallStrategy strategy,
        InstallRequest request,
        SemaphoreSlim limiter,
        IProgress<ItemProgress> progress,
        CancellationToken cancellationToken)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await strategy.PrepareAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }

    private async Task CleanupPrefetchAsync(IInstallStrategy strategy, Task<PreparedInstall> task)
    {
        try
        {
            var prepared = await task.ConfigureAwait(false);
            strategy.Cleanup(prepared);
        }
        catch (Exception ex) when (ex is OperationCanceledException or DownloadException)
        {
            // Canceled or failed downloads clean up their partial files themselves.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Önceden indirilen dosya temizlenemedi");
        }
    }

    private IInstallStrategy GetStrategy(PlanItem item) =>
        item.Method is { } method && _strategies.TryGetValue(method, out var strategy)
            ? strategy
            : throw new InvalidOperationException($"'{item.Method}' yöntemi için kurulum stratejisi yok.");

    private static InstallRequest CreateRequest(PlanItem item, InstallRunOptions options) =>
        new(item.Software, item.Method!.Value, item.AllowUpgrade, item.SizeBytes, options.DownloadDirectory, options.KeepDownloadedFiles);

    private static IProgress<ItemProgress> ItemProgressReporter(IProgress<SessionEvent>? events, string softwareId) =>
        new InlineProgress<ItemProgress>(p => events?.Report(new ItemProgressEvent(softwareId, p)));
}

/// <summary>An <see cref="IProgress{T}"/> that invokes the callback synchronously.</summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
