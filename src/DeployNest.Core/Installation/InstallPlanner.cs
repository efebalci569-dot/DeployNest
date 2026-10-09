using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;

namespace DeployNest.Core.Installation;

public enum PlanDecision
{
    Install,
    SkipInstalled,
    Unavailable,
}

public sealed record PlanItem(
    SoftwareEntry Software,
    PlanDecision Decision,
    InstallMethod? Method,
    FailureKind Reason,
    PackageVerification? Verification,
    bool AllowUpgrade,
    bool RunsUnattended)
{
    public long? SizeBytes => Verification?.SizeBytes ?? (Method == InstallMethod.Direct ? Software.Direct?.SizeBytes : null);
}

public sealed record InstallPlan(IReadOnlyList<PlanItem> Items)
{
    public IEnumerable<PlanItem> ToInstall => Items.Where(i => i.Decision == PlanDecision.Install);

    public long KnownDownloadBytes => ToInstall.Sum(i => i.SizeBytes ?? 0);

    public int UnknownSizeCount => ToInstall.Count(i => i.SizeBytes is null);
}

public sealed record PlannerInput(
    SoftwareEntry Software,
    InstallState State,
    bool ReinstallRequested,
    PackageVerification? Verification);

/// <summary>Turns the user's selection into an ordered, de-duplicated installation plan.</summary>
public static class InstallPlanner
{
    public static InstallMethod? ChooseMethod(SoftwareEntry software, bool wingetAvailable)
    {
        if (software.Winget is not null && wingetAvailable)
            return InstallMethod.Winget;
        if (software.Direct is not null)
            return InstallMethod.Direct;
        return null;
    }

    /// <summary>
    /// Builds the plan. Rules:
    /// <list type="bullet">
    /// <item>Each program appears once, even if selected twice.</item>
    /// <item>Installed programs are skipped unless a reinstall/upgrade was explicitly requested.</item>
    /// <item>winget packages must have passed verification; failed verification blocks installation.</item>
    /// <item>Unattended installs run first, installers that need user interaction run last;
    ///       within each group the catalog's category and entry order is kept.</item>
    /// </list>
    /// </summary>
    public static InstallPlan Create(IEnumerable<PlannerInput> inputs, bool wingetAvailable, IReadOnlyList<CatalogCategory> categories)
    {
        var categoryOrder = categories.ToDictionary(c => c.Id, c => c.Order);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<PlanItem>();

        foreach (var input in inputs)
        {
            var software = input.Software;
            if (!seen.Add(software.Id))
                continue;

            var method = ChooseMethod(software, wingetAvailable);
            var unattended = method switch
            {
                InstallMethod.Winget => true,
                InstallMethod.Direct => software.Direct!.RunsUnattended,
                _ => false,
            };

            PlanItem item;
            if (method is null)
            {
                var reason = software.Winget is not null ? FailureKind.WingetMissing : FailureKind.NoInstallMethod;
                item = new PlanItem(software, PlanDecision.Unavailable, null, reason, input.Verification, false, unattended);
            }
            else if (input.State == InstallState.Installed && !input.ReinstallRequested)
            {
                item = new PlanItem(software, PlanDecision.SkipInstalled, method, FailureKind.AlreadyInstalled, input.Verification, false, unattended);
            }
            else if (method == InstallMethod.Winget && input.Verification is not { Verified: true })
            {
                var reason = input.Verification?.Problem is { } p && p != FailureKind.None ? p : FailureKind.VerificationFailed;
                item = new PlanItem(software, PlanDecision.Unavailable, method, reason, input.Verification, false, unattended);
            }
            else
            {
                var allowUpgrade = input.State == InstallState.Installed && input.ReinstallRequested;
                item = new PlanItem(software, PlanDecision.Install, method, FailureKind.None, input.Verification, allowUpgrade, unattended);
            }

            items.Add(item);
        }

        int CategoryRank(PlanItem i) => categoryOrder.TryGetValue(i.Software.CategoryId, out var o) ? o : int.MaxValue;

        var ordered = items
            .OrderBy(i => i.Decision == PlanDecision.Install ? 0 : 1)
            .ThenBy(i => i.RunsUnattended ? 0 : 1)
            .ThenBy(CategoryRank)
            .ThenBy(i => i.Software.Order)
            .ToList();

        return new InstallPlan(ordered);
    }
}
