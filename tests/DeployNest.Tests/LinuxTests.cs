using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Core.Installation;
using DeployNest.Core.Linux;
using DeployNest.Core.Localization;
using DeployNest.Infrastructure.Linux;

namespace DeployNest.Tests;

public class LinuxSystemTests
{
    [Fact]
    public void Parses_os_release_with_quotes_and_comments()
    {
        var distro = LinuxDistribution.Parse("""
            # comment
            NAME="Linux Mint"
            VERSION_ID="22.2"
            ID=linuxmint
            ID_LIKE="ubuntu debian"
            PRETTY_NAME="Linux Mint 22.2"
            """);

        Assert.Equal("linuxmint", distro.Id);
        Assert.Equal(["ubuntu", "debian"], distro.IdLike);
        Assert.Equal("Linux Mint 22.2", distro.PrettyName);
        Assert.Equal("22.2", distro.VersionId);
    }

    [Theory]
    [InlineData("ID=ubuntu", LinuxPackageManager.Apt)]
    [InlineData("ID=pop\nID_LIKE=\"ubuntu debian\"", LinuxPackageManager.Apt)]
    [InlineData("ID=fedora", LinuxPackageManager.Dnf)]
    [InlineData("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"", LinuxPackageManager.Dnf)]
    [InlineData("ID=endeavouros\nID_LIKE=arch", LinuxPackageManager.Pacman)]
    public void Picks_package_manager_by_distribution_family(string osRelease, LinuxPackageManager expected)
    {
        var distro = LinuxDistribution.Parse(osRelease);

        Assert.Equal(expected, distro.ChoosePackageManager(_ => true));
    }

    [Fact]
    public void Family_tool_must_exist_and_unknown_family_uses_first_available_tool()
    {
        Assert.Null(LinuxDistribution.Parse("ID=ubuntu").ChoosePackageManager(m => m != LinuxPackageManager.Apt));
        Assert.Equal(LinuxPackageManager.Pacman, LinuxDistribution.Parse("ID=somethingnew").ChoosePackageManager(m => m == LinuxPackageManager.Pacman));
        Assert.Null(LinuxDistribution.Parse("ID=opensuse-tumbleweed\nID_LIKE=\"opensuse suse\"").ChoosePackageManager(_ => false));
    }
}

public class LinuxParserTests
{
    [Fact]
    public void Parses_installed_flatpak_apps()
    {
        var apps = LinuxParsers.ParseFlatpakList("org.videolan.VLC\t3.0.21\ncom.valvesoftware.Steam\t\nApplication ID\tVersion\n");

        Assert.Equal(2, apps.Count);
        Assert.Equal("3.0.21", apps["org.videolan.VLC"]);
        Assert.Null(apps["com.valvesoftware.Steam"]);
    }

    [Fact]
    public void Parses_native_package_lists()
    {
        var dpkg = LinuxParsers.ParseDpkgQuery("vlc\t3.0.20-3\tii \ngimp\t2.10\trc \nhtop\t3.3.0\thi \n");
        Assert.Equal(["htop", "vlc"], dpkg.Keys.Order());

        var rpm = LinuxParsers.ParseRpmQuery("git\t2.51.0\nbash\t5.3\nfoo\t\n");
        Assert.Equal("2.51.0", rpm["git"]);
        Assert.Null(rpm["foo"]);
        var pacman = LinuxParsers.ParsePacmanQuery("vlc 3.0.21-15\ngit 2.51.0-1\n");
        Assert.Equal("3.0.21-15", pacman["vlc"]);
    }

    [Fact]
    public void Parses_flatpak_remote_info()
    {
        var info = LinuxParsers.ParseFlatpakRemoteInfo("""
            VLC - VLC media player

                      ID: org.videolan.VLC
                     Ref: app/org.videolan.VLC/x86_64/stable
                 Version: 3.0.21
                 License: GPL-2.0+
                Download: 32.1?MB
               Installed: 76.0?MB
            """);

        Assert.Equal("3.0.21", info.Version);
        Assert.Equal(32_100_000, info.DownloadBytes);
        Assert.Equal("GPL-2.0+", info.License);
    }

    [Fact]
    public void Parses_repository_package_info()
    {
        var apt = LinuxParsers.ParseAptCacheShow("Package: vlc\nVersion: 3.0.20-3build6\nSize: 18852\n\nPackage: vlc\nVersion: 2.0\n");
        Assert.Equal("3.0.20-3build6", apt!.Version);
        Assert.Equal(18852, apt.DownloadBytes);
        Assert.Null(LinuxParsers.ParseAptCacheShow("N: Unable to locate package nope"));

        var pacman = LinuxParsers.ParsePacmanInfo("Repository      : extra\nName            : vlc\nVersion         : 3.0.21-15\nLicenses        : GPL-2.0-or-later\nDownload Size   : 1.50 MiB\n");
        Assert.Equal("3.0.21-15", pacman!.Version);
        Assert.Equal(1_572_864, pacman.DownloadBytes);

        var dnf = LinuxParsers.ParseDnfInfo("Name            : vlc\nVersion         : 3.0.21\nDownload size   : 2.0 MiB\nLicense         : GPLv2+\n");
        Assert.Equal("3.0.21", dnf!.Version);
        Assert.Equal(2_097_152, dnf.DownloadBytes);
    }

    [Theory]
    [InlineData("512.0 kB", 512_000L)]
    [InlineData("1.2 GB", 1_200_000_000L)]
    [InlineData("12 M", 12_582_912L)]
    [InlineData("3,5 MB", 3_500_000L)]
    [InlineData("930.5?kB", 930_500L)]
    [InlineData("1.4\u00a0MB", 1_400_000L)]
    [InlineData("2.1\u202fGB", 2_100_000_000L)]
    [InlineData("100", 100L)]
    [InlineData("bilinmiyor", null)]
    [InlineData("", null)]
    public void Parses_sizes(string text, long? expected)
    {
        Assert.Equal(expected, LinuxParsers.ParseSize(text));
    }

    [Theory]
    [InlineData("Installing 1/2… ████████▍   42%  1.2 MB/s  00:12", 42d)]
    [InlineData("Installing… 100%", 100d)]
    [InlineData("Looking for matches…", null)]
    public void Parses_progress_percentage(string line, double? expected)
    {
        Assert.Equal(expected, LinuxParsers.ParsePercent(line));
    }

    [Theory]
    [InlineData(LinuxTool.Apt, 100, "E: Unable to locate package nope", FailureKind.PackageNotFound)]
    [InlineData(LinuxTool.Apt, 100, "E: Could not get lock /var/lib/dpkg/lock-frontend. It is held by process 1234", FailureKind.InstallInProgress)]
    [InlineData(LinuxTool.Apt, 100, "The following packages have unmet dependencies:", FailureKind.DependencyFailed)]
    [InlineData(LinuxTool.Apt, 100, "Temporary failure resolving 'archive.ubuntu.com'", FailureKind.NetworkUnavailable)]
    [InlineData(LinuxTool.Dnf, 1, "No match for argument: nope", FailureKind.PackageNotFound)]
    [InlineData(LinuxTool.Pacman, 1, "error: target not found: nope", FailureKind.PackageNotFound)]
    [InlineData(LinuxTool.Pacman, 1, "error: failed to init transaction (unable to lock database)", FailureKind.InstallInProgress)]
    [InlineData(LinuxTool.Flatpak, 1, "error: No remote refs found for ‘org.nope.App’", FailureKind.PackageNotFound)]
    [InlineData(LinuxTool.Flatpak, 1, "error: No space left on device", FailureKind.DiskFull)]
    [InlineData(LinuxTool.Flatpak, 1, "something strange", FailureKind.InstallerFailed)]
    [InlineData(LinuxTool.Apt, 0, "", FailureKind.None)]
    public void Classifies_package_manager_failures(LinuxTool tool, int exitCode, string output, FailureKind expected)
    {
        Assert.Equal(expected, LinuxFailureClassifier.Classify(tool, exitCode, output));
    }
}

public class LinuxDetectionAndPlanningTests
{
    private static readonly SoftwareCatalog Catalog = CatalogParser.Parse("""
        {
          "schemaVersion": 1,
          "categories": [ { "id": "media", "name": { "tr": "Medya" } }, { "id": "dev", "name": { "tr": "Geliştirici" } } ],
          "software": [
            { "id": "vlc", "name": "VLC", "publisher": "VideoLAN", "category": "media", "summary": { "tr": "v" },
              "winget": { "id": "VideoLAN.VLC", "publisher": "VideoLAN" },
              "linux": { "flatpak": "org.videolan.VLC", "apt": [ "vlc" ], "dnf": [ "vlc" ], "pacman": [ "vlc" ] } },
            { "id": "spotify", "name": "Spotify", "publisher": "Spotify", "category": "media", "summary": { "tr": "s" },
              "linux": { "flatpak": "com.spotify.Client" } },
            { "id": "build", "name": "Build", "publisher": "GNU", "category": "dev", "summary": { "tr": "b" },
              "linux": { "apt": [ "build-essential" ], "dnf": [ "gcc", "make" ] } },
            { "id": "windows-only", "name": "W", "publisher": "P", "category": "dev", "summary": { "tr": "w" },
              "winget": { "id": "W.W", "publisher": "P" } }
          ]
        }
        """).Catalog!;

    private static readonly LinuxInstallationDetector Detector = new(new JsonLocalizer());

    private static SoftwareEntry Get(string id) => Catalog.FindSoftware(id)!;

    private static Dictionary<string, string?> Set(params string[] names) => names.ToDictionary(n => n, _ => (string?)"1.0");

    [Fact]
    public void Detects_flatpak_and_native_installations()
    {
        var inventory = new LinuxInventory(Set("org.videolan.VLC"), Set("gcc", "make"), LinuxPackageManager.Dnf);

        Assert.Equal(InstallState.Installed, Detector.Evaluate(Get("vlc"), inventory).State);
        Assert.Equal(InstallState.NotInstalled, Detector.Evaluate(Get("spotify"), inventory).State);
        Assert.Equal(InstallState.Installed, Detector.Evaluate(Get("build"), inventory).State);
    }

    [Fact]
    public void All_native_packages_must_be_installed_and_unknown_sources_are_unverifiable()
    {
        var partial = new LinuxInventory(null, Set("gcc"), LinuxPackageManager.Dnf);
        Assert.Equal(InstallState.NotInstalled, Detector.Evaluate(Get("build"), partial).State);

        Assert.Equal(InstallState.Unverifiable, Detector.Evaluate(Get("spotify"), new LinuxInventory(null, Set(), LinuxPackageManager.Apt)).State);
        Assert.Equal(InstallState.Unverifiable, Detector.Evaluate(Get("windows-only"), LinuxInventory.Empty).State);
    }

    private static PlannerInput Input(string id, InstallState state = InstallState.NotInstalled, bool verified = true) =>
        new(Get(id), state, false, verified ? TestData.Verified(10) : PackageVerification.Failed(FailureKind.PackageNotFound));

    [Fact]
    public void Prefers_flatpak_and_falls_back_to_native_packages()
    {
        var plan = InstallPlanner.Create([Input("vlc"), Input("spotify"), Input("build")],
            PlannerContext.Linux(flatpakAvailable: true, LinuxPackageManager.Apt), Catalog.Categories);

        Assert.Equal(InstallMethod.Flatpak, plan.Items.Single(i => i.Software.Id == "vlc").Method);
        Assert.Equal(InstallMethod.Flatpak, plan.Items.Single(i => i.Software.Id == "spotify").Method);
        Assert.Equal(InstallMethod.NativePackage, plan.Items.Single(i => i.Software.Id == "build").Method);
        Assert.All(plan.Items, i => Assert.Equal(PlanDecision.Install, i.Decision));
    }

    [Fact]
    public void Native_preference_and_missing_flatpak()
    {
        var native = InstallPlanner.Create([Input("vlc")], PlannerContext.Linux(true, LinuxPackageManager.Pacman, preferNative: true), Catalog.Categories);
        Assert.Equal(InstallMethod.NativePackage, native.Items.Single().Method);

        var noFlatpak = InstallPlanner.Create([Input("vlc"), Input("spotify"), Input("build")],
            PlannerContext.Linux(flatpakAvailable: false, LinuxPackageManager.Pacman), Catalog.Categories);
        Assert.Equal(InstallMethod.NativePackage, noFlatpak.Items.Single(i => i.Software.Id == "vlc").Method);
        var spotify = noFlatpak.Items.Single(i => i.Software.Id == "spotify");
        Assert.Equal(PlanDecision.Unavailable, spotify.Decision);
        Assert.Equal(FailureKind.FlatpakMissing, spotify.Reason);
        var build = noFlatpak.Items.Single(i => i.Software.Id == "build");
        Assert.Equal(FailureKind.UnsupportedDistribution, build.Reason);
    }

    [Fact]
    public void Linux_packages_must_be_verified()
    {
        var plan = InstallPlanner.Create([Input("vlc", verified: false)], PlannerContext.Linux(true, LinuxPackageManager.Apt), Catalog.Categories);

        Assert.Equal(PlanDecision.Unavailable, plan.Items.Single().Decision);
        Assert.Equal(FailureKind.PackageNotFound, plan.Items.Single().Reason);
    }

    [Fact]
    public void Failure_texts_prefer_the_linux_variant()
    {
        var localizer = new JsonLocalizer();
        var linux = new FailureDescriber(localizer, "Linux").Describe(FailureKind.PackageNotFound);
        var windows = new FailureDescriber(localizer).Describe(FailureKind.PackageNotFound);

        Assert.DoesNotContain("winget", linux.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget", windows.Title, StringComparison.OrdinalIgnoreCase);
        // Kinds without a Linux text fall back to the shared text.
        Assert.Equal(new FailureDescriber(localizer).Describe(FailureKind.DiskFull), new FailureDescriber(localizer, "Linux").Describe(FailureKind.DiskFull));
    }
}

public class RootHelperTests
{
    [Theory]
    [InlineData("vlc", true)]
    [InlineData("gcc-c++", true)]
    [InlineData("python3.12", true)]
    [InlineData("-y", false)]
    [InlineData("--allow-downgrades", false)]
    [InlineData("vlc;reboot", false)]
    [InlineData("vlc reboot", false)]
    [InlineData("$(id)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Accepts_only_plain_package_names(string? name, bool valid)
    {
        Assert.Equal(valid, RootHelper.IsValidPackageName(name));
    }

    [Fact]
    public void Install_commands_are_fixed_and_non_interactive()
    {
        var (tool, arguments, environment) = RootHelper.InstallCommand(LinuxPackageManager.Apt, ["vlc", "gimp"]);
        Assert.Equal("apt-get", tool);
        Assert.Equal(["install", "-y"], arguments.Take(2));
        Assert.Equal(["vlc", "gimp"], arguments.TakeLast(2));
        Assert.Equal("noninteractive", environment["DEBIAN_FRONTEND"]);

        var pacman = RootHelper.InstallCommand(LinuxPackageManager.Pacman, ["vlc"]);
        Assert.Equal("pacman", pacman.Tool);
        Assert.Equal(["-S", "--needed", "--noconfirm", "vlc"], pacman.Arguments);
        Assert.Equal(["install", "-y", "gcc"], RootHelper.InstallCommand(LinuxPackageManager.Dnf, ["gcc"]).Arguments);
    }

    [Fact]
    public async Task Helper_refuses_to_run_without_root()
    {
        if (Environment.IsPrivilegedProcess)
            return; // CI containers may run as root; the check is about unprivileged callers.

        var output = new StringWriter();
        var code = await RootHelper.RunAsync(new StringReader("{\"op\":\"install\",\"packages\":[\"vlc\"]}\n"), output, CancellationToken.None);

        Assert.Equal(1, code);
        Assert.Equal("error", HelperMessage.TryParse(output.ToString().Trim())!.Type);
    }

    [Fact]
    public void Helper_messages_round_trip_and_garbage_is_ignored()
    {
        var message = new HelperMessage { Id = 3, Type = "done", ExitCode = 100 };

        var parsed = HelperMessage.TryParse(message.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(message, parsed with { Packages = null });
        Assert.Null(HelperMessage.TryParse("not json"));
        Assert.Null(HelperMessage.TryParse(""));
    }

    [Fact]
    public void Manual_command_is_offered_when_elevation_fails()
    {
        Assert.Equal("sudo apt install vlc gimp", NativePackageStrategy.ManualCommand(LinuxPackageManager.Apt, ["vlc", "gimp"], null));
        Assert.StartsWith("sudo pacman -S --needed --noconfirm vlc", NativePackageStrategy.ManualCommand(LinuxPackageManager.Pacman, ["vlc"], "pkexec"));
    }
}
