using System.Diagnostics;
using System.ComponentModel;

namespace DeployNest.Linux.Services;

/// <summary>Opens web pages, files and folders with the desktop's default application (xdg-open).</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try
        {
            var start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            start.ArgumentList.Add(target);
            using var _ = Process.Start(start);
        }
        catch (Win32Exception)
        {
            // No desktop opener available; nothing else to do.
        }
    }
}
