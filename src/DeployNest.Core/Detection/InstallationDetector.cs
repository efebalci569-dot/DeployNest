using System.Text.RegularExpressions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Localization;

namespace DeployNest.Core.Detection;

/// <summary>
/// Decides whether a catalog program is installed. A program is never matched by name alone:
/// registry rules combine an anchored display-name pattern with the publisher, and winget
/// matches use exact package identifiers.
/// </summary>
public sealed class InstallationDetector
{
    private readonly ILocalizer _localizer;

    public InstallationDetector(ILocalizer localizer)
    {
        _localizer = localizer;
    }

    public DetectionResult Evaluate(SoftwareEntry entry, DetectionSnapshot snapshot)
    {
        if (snapshot.Registry is not null)
        {
            var match = FindRegistryMatch(entry, snapshot.Registry);
            if (match is not null)
            {
                var evidence = _localizer.Format("Detection.Evidence.Registry",
                    match.DisplayName, match.Publisher ?? "?");
                return new DetectionResult(InstallState.Installed, match.DisplayVersion, evidence, match.DisplayIcon);
            }
        }

        if (snapshot.Winget is { Available: true } winget)
        {
            var id = entry.AllWingetIds.FirstOrDefault(winget.PackageIds.Contains);
            if (id is not null)
                return new DetectionResult(InstallState.Installed, null, _localizer.Format("Detection.Evidence.Winget", id), null);
        }

        var hasRegistryRules = entry.Detection.Registry.Count > 0;
        var hasWingetIds = entry.AllWingetIds.Any();
        var registryPending = snapshot.Registry is null && !snapshot.RegistryFailed;
        var wingetPending = snapshot.Winget is null;

        // A source that could still find the program has not answered yet.
        if ((registryPending && hasRegistryRules) || (wingetPending && hasWingetIds))
            return DetectionResult.Checking;

        var registryChecked = hasRegistryRules && snapshot.Registry is not null && !snapshot.RegistryFailed;
        var wingetChecked = hasWingetIds && snapshot.Winget is { Available: true };
        if (registryChecked || wingetChecked)
            return new DetectionResult(InstallState.NotInstalled, null, NotFoundEvidence(registryChecked, wingetChecked), null);

        return new DetectionResult(InstallState.Unverifiable, null, _localizer["Detection.Evidence.NoSource"], null);
    }

    private string NotFoundEvidence(bool registry, bool winget) => (registry, winget) switch
    {
        (true, true) => _localizer["Detection.Evidence.NotFoundBoth"],
        (true, false) => _localizer["Detection.Evidence.NotFoundRegistry"],
        _ => _localizer["Detection.Evidence.NotFoundWinget"],
    };

    public static InstalledProgram? FindRegistryMatch(SoftwareEntry entry, IReadOnlyList<InstalledProgram> programs)
    {
        foreach (var rule in entry.Detection.Registry)
        {
            foreach (var program in programs)
            {
                if (SafeMatch(rule.DisplayName, program.DisplayName) &&
                    (rule.Publisher is null || SafeMatch(rule.Publisher, program.Publisher ?? string.Empty)))
                {
                    return program;
                }
            }
        }
        return null;
    }

    private static bool SafeMatch(Regex regex, string input)
    {
        try { return regex.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }
}
