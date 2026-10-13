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

/// <summary>
/// Platform rules for <see cref="InstallPlanner"/>: which method installs a program, why a
/// program cannot be installed, and whether a method must pass verification first.
/// </summary>
public sealed record PlannerContext(
    Func<SoftwareEntry, InstallMethod?> ChooseMethod,
    Func<SoftwareEntry, FailureKind> UnavailableReason,
    Func<InstallMethod, bool> RequiresVerification,
    Func<SoftwareEntry, InstallMethod, bool> RunsUnattended)
{
    /// <summary>Windows: winget when available, otherwise a verified direct download.</summary>
    public static PlannerContext Windows(bool wingetAvailable) => new(
        software => InstallPlanner.ChooseMethod(software, wingetAvailable),
        software => software.Winget is not null ? FailureKind.WingetMissing : FailureKind.NoInstallMethod,
        method => method == InstallMethod.Winget,
        (software, method) => method switch
        {
            InstallMethod.Winget => true,
            InstallMethod.Direct => software.Direct!.RunsUnattended,
            _ => false,
        });

    /// <summary>
    /// Linux: Flathub (per user) and distribution packages (system-wide). Flatpak is used first
    /// unless <paramref name="preferNative"/> is set; a method is only chosen when its tool is
    /// present. Both methods are verified against their repositories before installation.
    /// </summary>
    public static PlannerContext Linux(bool flatpakAvailable, LinuxPackageManager? packageManager, bool preferNative = false) => new(
        software => InstallPlanner.ChooseLinuxMethod(software, flatpakAvailable, packageManager, preferNative),
        software => software.Linux is { FlatpakId: not null } && !flatpakAvailable &&
                    (packageManager is null || software.Linux.PackagesFor(packageManager.Value).Count == 0)
            ? FailureKind.FlatpakMissing
            : software.Linux is null ? FailureKind.NoInstallMethod : FailureKind.UnsupportedDistribution,
        _ => true,
        (_, _) => true);
}

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

    public static InstallMethod? ChooseLinuxMethod(SoftwareEntry software, bool flatpakAvailable, LinuxPackageManager? packageManager, bool preferNative)
    {
        if (software.Linux is not { } linux)
            return null;

        var flatpak = flatpakAvailable && linux.FlatpakId is not null;
        var native = packageManager is { } manager && linux.PackagesFor(manager).Count > 0;
        if (flatpak && (!preferNative || !native))
            return InstallMethod.Flatpak;
        if (native)
            return InstallMethod.NativePackage;
        return null;
    }

    /// <summary>Builds a Windows plan (see <see cref="Create(IEnumerable{PlannerInput}, PlannerContext, IReadOnlyList{CatalogCategory})"/>).</summary>
    public static InstallPlan Create(IEnumerable<PlannerInput> inputs, bool wingetAvailable, IReadOnlyList<CatalogCategory> categories) =>
        Create(inputs, PlannerContext.Windows(wingetAvailable), categories);

    /// <summary>
    /// Builds the plan. Rules:
    /// <list type="bullet">
    /// <item>Each program appears once, even if selected twice.</item>
    /// <item>Installed programs are skipped unless a reinstall/upgrade was explicitly requested.</item>
    /// <item>Methods that require verification (winget, Flatpak, distribution packages) must have
    ///       passed it; failed verification blocks installation.</item>
    /// <item>Unattended installs run first, installers that need user interaction run last;
    ///       within each group the catalog's category and entry order is kept.</item>
    /// </list>
    /// </summary>
    public static InstallPlan Create(IEnumerable<PlannerInput> inputs, PlannerContext context, IReadOnlyList<CatalogCategory> categories)
    {
        var categoryOrder = categories.ToDictionary(c => c.Id, c => c.Order);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<PlanItem>();

        foreach (var input in inputs)
        {
            var software = input.Software;
            if (!seen.Add(software.Id))
                continue;

            var method = context.ChooseMethod(software);
            var unattended = method is { } m && context.RunsUnattended(software, m);

            PlanItem item;
            if (method is null)
            {
                item = new PlanItem(software, PlanDecision.Unavailable, null, context.UnavailableReason(software), input.Verification, false, unattended);
            }
            else if (input.State == InstallState.Installed && !input.ReinstallRequested)
            {
                item = new PlanItem(software, PlanDecision.SkipInstalled, method, FailureKind.AlreadyInstalled, input.Verification, false, unattended);
            }
            else if (context.RequiresVerification(method.Value) && input.Verification is not { Verified: true })
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
