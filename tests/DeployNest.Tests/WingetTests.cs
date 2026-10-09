using DeployNest.Core.Abstractions;
using DeployNest.Core.Installation;
using DeployNest.Core.Winget;
using DeployNest.Infrastructure.Verification;
using DeployNest.Infrastructure.Winget;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeployNest.Tests;

public class WingetPackageIdTests
{
    [Theory]
    [InlineData("Google.Chrome")]
    [InlineData("7zip.7zip")]
    [InlineData("Python.Python.3.14")]
    [InlineData("Microsoft.DotNet.SDK.10")]
    [InlineData("Mozilla.Firefox.tr")]
    [InlineData("Notepad++.Notepad++")]
    public void Accepts_real_identifiers(string id) => Assert.True(WingetPackageId.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Chrome")]
    [InlineData("Google Chrome")]
    [InlineData("Google.Chrome;calc")]
    [InlineData("Google.Chrome && calc.exe")]
    [InlineData("-Google.Chrome")]
    [InlineData("Google..Chrome")]
    [InlineData("../evil.pkg")]
    [InlineData("Google.Chrome\"")]
    public void Rejects_malformed_or_dangerous_identifiers(string? id) => Assert.False(WingetPackageId.IsValid(id));

    [Fact]
    public void Rejects_overlong_identifiers() =>
        Assert.False(WingetPackageId.IsValid("A." + new string('b', WingetPackageId.MaxLength)));
}

public class WingetParserTests
{
    private const string DiscordShow = """
        Found Discord [Discord.Discord]
        Version: 1.0.9261
        Publisher: Discord Inc.
        Publisher Url: https://discord.com/
        Author: Discord Inc.
        Moniker: discord
        Description:
          Discord is the easiest way to talk over voice, video, and text.
        Homepage: https://discord.com/download
        License: Proprietary
        License Url: https://discord.com/terms
        Tags:
          chat
          voice
        Installer:
          Installer Type: exe
          Installer Url: https://dl.discordapp.net/distro/app/stable/win/x64/1.0.9261/DiscordSetup.exe
          Installer SHA256: e6d2f3f781904b6f336a2aa22699d1c97cecab3784509804e870ac4cb69f2ac2
          Release Date: 2026-10-05
          Offline Distribution Supported: true
        """;

    [Fact]
    public void Parses_show_output()
    {
        var info = WingetShowParser.Parse(DiscordShow)!;

        Assert.Equal("Discord.Discord", info.Id);
        Assert.Equal("Discord", info.Name);
        Assert.Equal("1.0.9261", info.Version);
        Assert.Equal("Discord Inc.", info.Publisher);
        Assert.Equal("Proprietary", info.License);
        Assert.Equal("https://discord.com/terms", info.LicenseUrl);
        Assert.Equal("exe", info.InstallerType);
        Assert.StartsWith("https://dl.discordapp.net/", info.InstallerUrl);
        Assert.Equal("e6d2f3f781904b6f336a2aa22699d1c97cecab3784509804e870ac4cb69f2ac2", info.InstallerSha256);
        Assert.Empty(info.Agreements);
    }

    [Fact]
    public void Parses_dependencies_agreements_and_spinner_noise()
    {
        const string output = "   - \r   \\ \r   | \r\nFound LibreOffice [TheDocumentFoundation.LibreOffice]\r\nVersion: 26.8.1.1\r\nPublisher: The Document Foundation\r\nAgreements:\r\n  Category: Utilities\r\n  Terms of Transaction: https://example.com/terms\r\nInstaller:\r\n  Installer Type: msi\r\n  Dependencies: \r\n    - Package Dependencies: \r\n        Microsoft.VCRedist.2015+.x64\r\n";

        var info = WingetShowParser.Parse(output)!;

        Assert.Equal("TheDocumentFoundation.LibreOffice", info.Id);
        Assert.Equal(["Microsoft.VCRedist.2015+.x64"], info.Dependencies);
        Assert.Equal(2, info.Agreements.Count);
        Assert.Equal("msi", info.InstallerType);
    }

    [Fact]
    public void Localized_output_still_yields_the_identifier_but_no_guessed_fields()
    {
        const string output = "Bulundu Discord [Discord.Discord]\nSürüm: 1.0.9261\nYayımcı: Discord Inc.\n";

        var info = WingetShowParser.Parse(output)!;

        Assert.Equal("Discord.Discord", info.Id);
        Assert.Null(info.Publisher);
        Assert.Null(info.Version);
    }

    [Fact]
    public void Unrecognized_output_returns_null() => Assert.Null(WingetShowParser.Parse("No package found matching input criteria."));

    [Fact]
    public void Parses_export_json()
    {
        const string json = """
            { "$schema": "x", "Sources": [ { "Packages": [ { "PackageIdentifier": "Git.Git" }, { "PackageIdentifier": "Valve.Steam" } ], "SourceDetails": {} } ] }
            """;

        var ids = WingetExportParser.Parse(json);

        Assert.True(ids.Contains("git.git"));
        Assert.True(ids.Contains("Valve.Steam"));
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public void Progress_interpreter_follows_real_install_output()
    {
        var interpreter = new WingetProgressInterpreter();
        string[] lines =
        [
            "Found Microsoft .NET SDK 10.0 [Microsoft.DotNet.SDK.10] Version 10.0.401",
            "This application is licensed to you by its owner.",
            "Downloading https://download.microsoft.com/download/x/dotnet-sdk-10.0.401-win-x64.exe",
            "Successfully verified installer hash",
            "Starting package install...",
            "Successfully installed",
        ];

        var phases = lines.Select(l => interpreter.OnLine(l)?.Phase).ToList();

        Assert.Null(phases[0]);
        Assert.Null(phases[1]);
        Assert.Equal(ItemPhase.Downloading, phases[2]);
        Assert.Equal(ItemPhase.Verifying, phases[3]);
        Assert.Equal(ItemPhase.Installing, phases[4]);
        Assert.Null(phases[5]);
        Assert.Equal("download.microsoft.com", interpreter.DownloadHost);
    }

    [Fact]
    public void Progress_interpreter_reads_byte_progress_bars()
    {
        var progress = new WingetProgressInterpreter().OnLine("  ██████████▒▒▒▒▒▒▒▒  60.0 MB /  120 MB")!;

        Assert.Equal(ItemPhase.Downloading, progress.Phase);
        Assert.Equal(50, progress.Percent!.Value, 1);
        Assert.Equal(120L * 1024 * 1024, progress.TotalBytes);
    }

    [Theory]
    [InlineData(0, OutcomeStatus.Succeeded, FailureKind.None)]
    [InlineData(unchecked((int)0x8A150061), OutcomeStatus.Skipped, FailureKind.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A15002B), OutcomeStatus.Skipped, FailureKind.AlreadyInstalled)]
    [InlineData(unchecked((int)0x8A150014), OutcomeStatus.Failed, FailureKind.PackageNotFound)]
    [InlineData(unchecked((int)0x8A150011), OutcomeStatus.Failed, FailureKind.HashMismatch)]
    [InlineData(unchecked((int)0x8A150105), OutcomeStatus.Failed, FailureKind.DiskFull)]
    [InlineData(unchecked((int)0x8A150107), OutcomeStatus.Failed, FailureKind.NetworkUnavailable)]
    [InlineData(unchecked((int)0x8A15010C), OutcomeStatus.Failed, FailureKind.CancelledInInstaller)]
    [InlineData(unchecked((int)0x8A150101), OutcomeStatus.Failed, FailureKind.PackageInUse)]
    [InlineData(unchecked((int)0x8A150008), OutcomeStatus.Failed, FailureKind.DownloadFailed)]
    [InlineData(1603, OutcomeStatus.Failed, FailureKind.InstallerFailed)]
    public void Exit_codes_map_to_outcomes(int code, OutcomeStatus status, FailureKind failure)
    {
        var outcome = WingetExitCodes.ToOutcome(code);

        Assert.Equal(status, outcome.Status);
        Assert.Equal(failure, outcome.Failure);
    }

    [Fact]
    public void Reboot_required_codes_count_as_success()
    {
        var outcome = WingetExitCodes.ToOutcome(unchecked((int)0x8A150109));

        Assert.Equal(OutcomeStatus.Succeeded, outcome.Status);
        Assert.True(outcome.RebootRequired);
    }

    [Theory]
    [InlineData("Discord Inc.", "Discord Inc", true)]
    [InlineData("Google LLC", "google llc", true)]
    [InlineData("Google LLC Fake", "Google LLC", false)]
    public void Publisher_comparison_ignores_punctuation_only(string actual, string expected, bool match) =>
        Assert.Equal(match, PackageVerifier.PublisherMatches(actual, expected));
}

public class WingetClientTests
{
    private sealed class FakeLocator(bool available) : IWingetLocator
    {
        public Task<WingetStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(available ? new WingetStatus(true, "1.29.0", @"C:\winget.exe", null) : new WingetStatus(false, null, null, "notFound"));
    }

    private sealed class RecordingRunner(int exitCode, string output = "") : IProcessRunner
    {
        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessResult(exitCode, output, string.Empty));
        }
    }

    private static WingetClient Client(RecordingRunner runner, bool available = true) =>
        new(new FakeLocator(available), runner, NullLogger<WingetClient>.Instance);

    [Fact]
    public async Task Install_passes_arguments_as_a_list_and_blocks_silent_upgrades()
    {
        var runner = new RecordingRunner(0);

        await Client(runner).InstallAsync("Google.Chrome", allowUpgrade: false, _ => { }, CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.False(request.UseShellExecute);
        Assert.Equal(
            ["install", "--id", "Google.Chrome", "--exact", "--source", "winget", "--accept-source-agreements", "--disable-interactivity", "--silent", "--accept-package-agreements", "--no-upgrade"],
            request.Arguments);
    }

    [Fact]
    public async Task Upgrade_is_only_allowed_when_requested()
    {
        var runner = new RecordingRunner(0);

        await Client(runner).InstallAsync("Google.Chrome", allowUpgrade: true, _ => { }, CancellationToken.None);

        Assert.DoesNotContain("--no-upgrade", runner.Requests.Single().Arguments);
    }

    [Fact]
    public async Task Invalid_identifier_never_reaches_the_process()
    {
        var runner = new RecordingRunner(0);

        await Assert.ThrowsAsync<ArgumentException>(() => Client(runner).InstallAsync("Google.Chrome & calc", false, _ => { }, CancellationToken.None));
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Inventory_reports_unavailable_when_winget_is_missing()
    {
        var runner = new RecordingRunner(0);

        var inventory = await Client(runner, available: false).GetInventoryAsync(CancellationToken.None);

        Assert.False(inventory.Available);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(unchecked((int)0x8A150014), false)]
    [InlineData(unchecked((int)0x8A150001), null)]
    public async Task IsInstalled_maps_exit_codes(int code, bool? expected)
    {
        Assert.Equal(expected, await Client(new RecordingRunner(code)).IsInstalledAsync("Git.Git", CancellationToken.None));
    }
}
