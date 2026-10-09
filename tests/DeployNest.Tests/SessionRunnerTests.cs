using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeployNest.Tests;

public class SessionRunnerTests
{
    private static readonly SoftwareCatalog Catalog = TestData.Catalog();
    private static readonly InstallRunOptions Options = new(2, Path.GetTempPath(), false);

    private static InstallPlan Plan(bool wingetAvailable = true, params (string Id, InstallState State)[] items) =>
        InstallPlanner.Create(
            items.Select(i => new PlannerInput(Catalog.FindSoftware(i.Id)!, i.State, false, TestData.Verified())),
            wingetAvailable,
            Catalog.Categories);

    private static InstallPlan AllThree() =>
        Plan(true, ("chrome", InstallState.NotInstalled), ("seven-zip", InstallState.NotInstalled), ("turkce-araci", InstallState.NotInstalled));

    private static InstallSessionRunner Runner(FakeStrategy strategy, PostInstallVerifier? verifier = null) =>
        new([strategy], new FixedClock(), NullLogger<InstallSessionRunner>.Instance, verifier);

    [Fact]
    public async Task Installs_every_item_in_plan_order_and_reports_events()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        var events = new List<SessionEvent>();
        using var cancellation = new SessionCancellation();

        var result = await Runner(strategy).RunAsync(AllThree(), Options, new InlineProgress<SessionEvent>(events.Add), cancellation);

        Assert.Equal(["chrome", "seven-zip", "turkce-araci"], strategy.InstallOrder);
        Assert.Equal(3, result.Succeeded.Count());
        Assert.Equal(3, events.OfType<ItemCompletedEvent>().Count());
        Assert.Contains(events.OfType<ItemProgressEvent>(), e => e.Progress.Phase == ItemPhase.Installing);
        Assert.False(result.CancelRequested);
    }

    [Fact]
    public async Task A_failure_does_not_stop_the_remaining_items()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        strategy.Behaviors["chrome"] = _ => Task.FromResult(InstallOutcome.Fail(FailureKind.InstallerFailed, "1603", 1603));
        strategy.Behaviors["seven-zip"] = _ => throw new InvalidOperationException("boom");
        using var cancellation = new SessionCancellation();

        var result = await Runner(strategy).RunAsync(AllThree(), Options, null, cancellation);

        Assert.Equal(3, strategy.InstallOrder.Count);
        Assert.Equal(FailureKind.InstallerFailed, result.Items[0].Outcome.Failure);
        Assert.Equal(FailureKind.Unknown, result.Items[1].Outcome.Failure);
        Assert.Equal(OutcomeStatus.Succeeded, result.Items[2].Outcome.Status);
    }

    [Fact]
    public async Task Stop_lets_the_running_installer_finish_and_cancels_the_rest()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        strategy.Behaviors["seven-zip"] = async _ =>
        {
            started.SetResult();
            await release.Task;
            return InstallOutcome.Success();
        };
        using var cancellation = new SessionCancellation();

        var run = Runner(strategy).RunAsync(AllThree(), Options, null, cancellation);
        await started.Task;
        cancellation.RequestStop();
        release.SetResult();
        var result = await run;

        Assert.Equal(OutcomeStatus.Succeeded, result.Items.Single(i => i.Software.Id == "seven-zip").Outcome.Status);
        Assert.Equal(OutcomeStatus.Canceled, result.Items.Single(i => i.Software.Id == "turkce-araci").Outcome.Status);
        Assert.DoesNotContain("turkce-araci", strategy.InstallOrder);
        Assert.True(result.CancelRequested);
    }

    [Fact]
    public async Task Abort_cancels_the_running_installer()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        var started = new TaskCompletionSource();
        strategy.Behaviors["chrome"] = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return InstallOutcome.Success();
        };
        using var cancellation = new SessionCancellation();

        var run = Runner(strategy).RunAsync(AllThree(), Options, null, cancellation);
        await started.Task;
        cancellation.RequestAbort();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(result.Items, i => Assert.Equal(OutcomeStatus.Canceled, i.Outcome.Status));
        Assert.Equal(["chrome"], strategy.InstallOrder);
    }

    [Fact]
    public async Task Skipped_and_unavailable_items_are_reported_without_running()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        var plan = InstallPlanner.Create(
            [
                new PlannerInput(Catalog.FindSoftware("chrome")!, InstallState.Installed, false, TestData.Verified()),
                new PlannerInput(Catalog.FindSoftware("turkce-araci")!, InstallState.NotInstalled, false, PackageVerification.Failed(FailureKind.PackageNotFound)),
                new PlannerInput(Catalog.FindSoftware("seven-zip")!, InstallState.NotInstalled, false, TestData.Verified()),
            ],
            true,
            Catalog.Categories);
        using var cancellation = new SessionCancellation();

        var result = await Runner(strategy).RunAsync(plan, Options, null, cancellation);

        Assert.Equal(["seven-zip"], strategy.InstallOrder);
        Assert.Equal(FailureKind.AlreadyInstalled, result.Skipped.Single().Outcome.Failure);
        Assert.Equal(FailureKind.PackageNotFound, result.Failed.Single().Outcome.Failure);
    }

    [Fact]
    public async Task Prevents_two_sessions_at_once_and_duplicate_items()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        var release = new TaskCompletionSource();
        strategy.Behaviors["chrome"] = async _ =>
        {
            await release.Task;
            return InstallOutcome.Success();
        };
        var runner = Runner(strategy);
        using var first = new SessionCancellation();
        using var second = new SessionCancellation();

        var running = runner.RunAsync(AllThree(), Options, null, first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(AllThree(), Options, null, second));
        release.SetResult();
        await running;

        var plan = AllThree();
        var duplicated = new InstallPlan([.. plan.Items, plan.Items[0]]);
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(duplicated, Options, null, second));
    }

    [Fact]
    public async Task Records_post_install_verification()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        using var cancellation = new SessionCancellation();
        PostInstallVerifier verifier = (software, _) => Task.FromResult<bool?>(software.Id != "chrome");

        var result = await Runner(strategy, verifier).RunAsync(AllThree(), Options, null, cancellation);

        Assert.False(result.Items.Single(i => i.Software.Id == "chrome").PostInstallVerified);
        Assert.True(result.Items.Single(i => i.Software.Id == "seven-zip").PostInstallVerified);
    }

    [Fact]
    public async Task Prefetching_strategy_prepares_ahead_and_cleans_up()
    {
        var direct = new FakeStrategy(InstallMethod.Direct, prefetch: true);
        var plan = Plan(false, ("seven-zip", InstallState.NotInstalled));
        using var cancellation = new SessionCancellation();

        var result = await new InstallSessionRunner([direct], new FixedClock(), NullLogger<InstallSessionRunner>.Instance)
            .RunAsync(plan, Options, null, cancellation);

        Assert.Equal(OutcomeStatus.Succeeded, result.Items.Single().Outcome.Status);
        Assert.Equal(["seven-zip"], direct.Prepared);
        Assert.Equal(["seven-zip"], direct.CleanedUp);
    }

    [Fact]
    public async Task Report_lists_every_group_with_advice()
    {
        var strategy = new FakeStrategy(InstallMethod.Winget);
        strategy.Behaviors["seven-zip"] = _ => Task.FromResult(InstallOutcome.Fail(FailureKind.ElevationDenied, "0x8A150019"));
        using var cancellation = new SessionCancellation();
        var plan = InstallPlanner.Create(
            [
                new PlannerInput(Catalog.FindSoftware("chrome")!, InstallState.Installed, false, TestData.Verified()),
                new PlannerInput(Catalog.FindSoftware("seven-zip")!, InstallState.NotInstalled, false, TestData.Verified()),
                new PlannerInput(Catalog.FindSoftware("turkce-araci")!, InstallState.NotInstalled, false, TestData.Verified()),
            ],
            true,
            Catalog.Categories);

        var result = await Runner(strategy).RunAsync(plan, Options, null, cancellation);
        var report = new ReportBuilder(new JsonLocalizer()).Build(result, "0.1.0", @"C:\logs");

        Assert.Contains("Başarıyla kurulanlar (1)", report);
        Assert.Contains("Başarısız olanlar (1)", report);
        Assert.Contains("Atlananlar (1)", report);
        Assert.Contains("İptal edilenler (0)", report);
        Assert.Contains("Yönetici izni verilmedi (kod: 0x8A150019)", report);
        Assert.Contains("Ne yapabilirsin:", report);
        Assert.Contains("Türkçe Şablon Aracı", report);
        Assert.Contains(@"C:\logs", report);
    }
}
