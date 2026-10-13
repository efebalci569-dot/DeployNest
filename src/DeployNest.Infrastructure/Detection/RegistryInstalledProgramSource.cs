using System.Runtime.Versioning;
using DeployNest.Core.Detection;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeployNest.Infrastructure.Detection;

/// <summary>Reads the standard "Uninstall" keys (64-bit, 32-bit and per-user).</summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryInstalledProgramSource(ILogger<RegistryInstalledProgramSource> logger) : IInstalledProgramSource
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<InstalledProgram>>(() =>
        {
            var programs = new List<InstalledProgram>();
            Read(RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM", programs, cancellationToken);
            Read(RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM (32-bit)", programs, cancellationToken);
            Read(RegistryHive.CurrentUser, RegistryView.Default, "HKCU", programs, cancellationToken);
            logger.LogInformation("Kayıt defterinden {Count} kurulu program okundu", programs.Count);
            return programs;
        }, cancellationToken);

    private void Read(RegistryHive hive, RegistryView view, string label, List<InstalledProgram> programs, CancellationToken cancellationToken)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null)
                return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var key = uninstall.OpenSubKey(name);
                if (key is null)
                    continue;

                if (key.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
                    continue;
                // Components and updates of other products are not standalone programs.
                if (key.GetValue("SystemComponent") is int and 1)
                    continue;
                if (key.GetValue("ParentKeyName") is string)
                    continue;

                programs.Add(new InstalledProgram(
                    displayName.Trim(),
                    (key.GetValue("Publisher") as string)?.Trim(),
                    (key.GetValue("DisplayVersion") as string)?.Trim(),
                    (key.GetValue("DisplayIcon") as string)?.Trim(),
                    label));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "{Hive} kaldırma kayıtları okunamadı", label);
        }
    }
}
