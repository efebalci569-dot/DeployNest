using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Installation;
using DeployNest.Core.Localization;

namespace DeployNest.Tests;

public class InstallationDetectorTests
{
    private static readonly SoftwareCatalog Catalog = TestData.Catalog();
    private static readonly InstallationDetector Detector = new(new JsonLocalizer());
    private static SoftwareEntry Chrome => Catalog.FindSoftware("chrome")!;

    private static WingetInventory Winget(params string[] ids) =>
        new(true, new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase), null);

    [Fact]
    public void Registry_match_requires_name_and_publisher()
    {
        var registry = new[] { new InstalledProgram("Google Chrome", "Google LLC", "155.0", "chrome.exe,0", "HKLM") };

        var result = Detector.Evaluate(Chrome, new DetectionSnapshot(registry, false, Winget()));

        Assert.Equal(InstallState.Installed, result.State);
        Assert.Equal("155.0", result.Version);
        Assert.Equal("chrome.exe,0", result.IconPath);
    }

    [Theory]
    [InlineData("Google Chrome", "Totally Not Google")]
    [InlineData("Google Chrome Beta", "Google LLC")]
    [InlineData("My Google Chrome Helper", "Google LLC")]
    public void Similar_names_are_not_false_positives(string name, string publisher)
    {
        var registry = new[] { new InstalledProgram(name, publisher, "1", null, "HKCU") };

        var result = Detector.Evaluate(Chrome, new DetectionSnapshot(registry, false, Winget()));

        Assert.Equal(InstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Winget_inventory_detects_by_exact_package_id()
    {
        var result = Detector.Evaluate(Chrome, new DetectionSnapshot([], false, Winget("google.chrome")));

        Assert.Equal(InstallState.Installed, result.State);
        Assert.Contains("Google.Chrome", result.Evidence);
    }

    [Fact]
    public void Waits_for_winget_before_reporting_not_installed()
    {
        Assert.Equal(InstallState.Checking, Detector.Evaluate(Chrome, new DetectionSnapshot(null, false, null)).State);
        Assert.Equal(InstallState.Checking, Detector.Evaluate(Chrome, new DetectionSnapshot([], false, null)).State);
        Assert.Equal(InstallState.NotInstalled, Detector.Evaluate(Chrome, new DetectionSnapshot([], false, Winget())).State);
    }

    [Fact]
    public void Registry_only_result_when_winget_is_unavailable()
    {
        var result = Detector.Evaluate(Chrome, new DetectionSnapshot([], false, WingetInventory.Unavailable("notFound")));

        Assert.Equal(InstallState.NotInstalled, result.State);
    }

    [Fact]
    public void Reports_unverifiable_when_no_source_can_answer()
    {
        var noRules = Catalog.FindSoftware("turkce-araci")!;

        var withoutWinget = Detector.Evaluate(noRules, new DetectionSnapshot([], false, WingetInventory.Unavailable("notFound")));
        var registryFailed = Detector.Evaluate(Chrome, new DetectionSnapshot(null, true, WingetInventory.Unavailable("notFound")));

        Assert.Equal(InstallState.Unverifiable, withoutWinget.State);
        Assert.Equal(InstallState.Unverifiable, registryFailed.State);
    }
}

public class InstallPlannerTests
{
    private static readonly SoftwareCatalog Catalog = TestData.Catalog();

    private static PlannerInput Input(string id, InstallState state = InstallState.NotInstalled, bool reinstall = false, PackageVerification? verification = null) =>
        new(Catalog.FindSoftware(id)!, state, reinstall, verification ?? TestData.Verified());

    [Fact]
    public void Orders_by_category_and_catalog_order_and_removes_duplicates()
    {
        var plan = InstallPlanner.Create(
            [Input("turkce-araci"), Input("seven-zip"), Input("chrome"), Input("seven-zip")],
            wingetAvailable: true,
            Catalog.Categories);

        Assert.Equal(["chrome", "seven-zip", "turkce-araci"], plan.Items.Select(i => i.Software.Id));
        Assert.All(plan.Items, i => Assert.Equal(PlanDecision.Install, i.Decision));
        Assert.All(plan.Items, i => Assert.Equal(InstallMethod.Winget, i.Method));
    }

    [Fact]
    public void Installed_programs_are_skipped_unless_reinstall_requested()
    {
        var plan = InstallPlanner.Create(
            [Input("chrome", InstallState.Installed), Input("seven-zip", InstallState.Installed, reinstall: true)],
            true,
            Catalog.Categories);

        var chrome = plan.Items.Single(i => i.Software.Id == "chrome");
        var sevenZip = plan.Items.Single(i => i.Software.Id == "seven-zip");
        Assert.Equal(PlanDecision.SkipInstalled, chrome.Decision);
        Assert.Equal(FailureKind.AlreadyInstalled, chrome.Reason);
        Assert.Equal(PlanDecision.Install, sevenZip.Decision);
        Assert.True(sevenZip.AllowUpgrade);
        Assert.Equal("seven-zip", plan.Items[0].Software.Id);
    }

    [Fact]
    public void Unverified_winget_packages_are_not_installed()
    {
        var plan = InstallPlanner.Create(
            [
                Input("chrome", verification: PackageVerification.Failed(FailureKind.PublisherMismatch, "Evil")),
                new PlannerInput(Catalog.FindSoftware("seven-zip")!, InstallState.NotInstalled, false, null),
            ],
            true,
            Catalog.Categories);

        Assert.All(plan.Items, i => Assert.Equal(PlanDecision.Unavailable, i.Decision));
        Assert.Equal(FailureKind.PublisherMismatch, plan.Items.Single(i => i.Software.Id == "chrome").Reason);
        Assert.Equal(FailureKind.VerificationFailed, plan.Items.Single(i => i.Software.Id == "seven-zip").Reason);
        Assert.Empty(plan.ToInstall);
    }

    [Fact]
    public void Without_winget_falls_back_to_direct_download_when_defined()
    {
        var plan = InstallPlanner.Create([Input("chrome"), Input("seven-zip")], wingetAvailable: false, Catalog.Categories);

        var sevenZip = plan.Items.Single(i => i.Software.Id == "seven-zip");
        var chrome = plan.Items.Single(i => i.Software.Id == "chrome");
        Assert.Equal(InstallMethod.Direct, sevenZip.Method);
        Assert.Equal(PlanDecision.Install, sevenZip.Decision);
        Assert.Equal(PlanDecision.Unavailable, chrome.Decision);
        Assert.Equal(FailureKind.WingetMissing, chrome.Reason);
        Assert.Equal(2009088, plan.KnownDownloadBytes);
        Assert.Equal(0, plan.UnknownSizeCount);
    }

    [Fact]
    public void Size_totals_count_unknown_sizes()
    {
        var plan = InstallPlanner.Create(
            [Input("chrome", verification: TestData.Verified(1000)), Input("turkce-araci", verification: TestData.Verified())],
            true,
            Catalog.Categories);

        Assert.Equal(1000, plan.KnownDownloadBytes);
        Assert.Equal(1, plan.UnknownSizeCount);
    }
}
