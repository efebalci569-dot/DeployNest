using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Infrastructure.Winget;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Verification;

/// <summary>
/// Confirms after a reported success that the program is really present: first through the
/// registry rules, then through "winget list --exact". Returns null when neither source can tell.
/// </summary>
public sealed class InstalledStateVerifier(
    IInstalledProgramSource registry,
    WingetClient winget,
    ILogger<InstalledStateVerifier> logger)
{
    public async Task<bool?> VerifyAsync(SoftwareEntry software, CancellationToken cancellationToken)
    {
        if (software.Detection.Registry.Count > 0)
        {
            try
            {
                var programs = await registry.GetInstalledProgramsAsync(cancellationToken).ConfigureAwait(false);
                if (InstallationDetector.FindRegistryMatch(software, programs) is not null)
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Kurulum sonrası kayıt defteri okunamadı");
            }
        }

        if (software.Winget is not null)
        {
            try
            {
                return await winget.IsInstalledAsync(software.Winget.PackageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Kurulum sonrası winget denetimi yapılamadı");
            }
        }

        return software.Detection.Registry.Count > 0 ? false : null;
    }
}
