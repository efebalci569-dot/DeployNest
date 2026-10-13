using System.Globalization;
using System.Text.RegularExpressions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;

namespace DeployNest.Core.Linux;

/// <summary>Package metadata read from Flathub or the distribution repositories.</summary>
public sealed record LinuxPackageInfo(string? Version, long? DownloadBytes, string? License);

/// <summary>
/// Parsers for the output of flatpak, dpkg-query/apt-cache, rpm/dnf and pacman. All commands are
/// run with LC_ALL=C so labels are in English regardless of the user's language.
/// </summary>
public static partial class LinuxParsers
{
    /// <summary>Output of <c>flatpak list --app --columns=application,version</c>.</summary>
    public static IReadOnlyDictionary<string, string?> ParseFlatpakList(string output)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var parts = line.Split('\t');
            if (parts.Length == 1)
                parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var id = parts[0].Trim();
            if (!FlatpakIdRegex().IsMatch(id))
                continue;
            result[id] = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;
        }
        return result;
    }

    /// <summary>Output of <c>dpkg-query -W -f='${Package}\t${Version}\t${db:Status-Abbrev}\n'</c>; only "ii"/"hi" count as installed.</summary>
    public static IReadOnlyDictionary<string, string?> ParseDpkgQuery(string output)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3)
                continue;
            var status = parts[2].Trim();
            if (status.StartsWith("ii", StringComparison.Ordinal) || status.StartsWith("hi", StringComparison.Ordinal))
                result[parts[0].Trim()] = parts[1].Trim();
        }
        return result;
    }

    /// <summary>Output of <c>rpm -qa --qf '%{NAME}\t%{VERSION}\n'</c>.</summary>
    public static IReadOnlyDictionary<string, string?> ParseRpmQuery(string output) => ParsePairs(output, '\t');

    /// <summary>Output of <c>pacman -Q</c> ("name version").</summary>
    public static IReadOnlyDictionary<string, string?> ParsePacmanQuery(string output) => ParsePairs(output, ' ');

    private static Dictionary<string, string?> ParsePairs(string output, char separator)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var parts = line.Split(separator, 2, StringSplitOptions.TrimEntries);
            if (parts[0].Length > 0)
                result[parts[0]] = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        }
        return result;
    }

    /// <summary>Output of <c>flatpak remote-info flathub &lt;id&gt;</c>.</summary>
    public static LinuxPackageInfo ParseFlatpakRemoteInfo(string output)
    {
        var fields = Fields(output, ':');
        return new LinuxPackageInfo(
            fields.GetValueOrDefault("Version"),
            ParseSize(fields.GetValueOrDefault("Download")),
            fields.GetValueOrDefault("License"));
    }

    /// <summary>First record of <c>apt-cache show --no-all-versions &lt;pkg&gt;</c>; Size is in bytes.</summary>
    public static LinuxPackageInfo? ParseAptCacheShow(string output)
    {
        var record = output.Split("\n\n", 2)[0];
        var fields = Fields(record, ':');
        if (!fields.ContainsKey("Package"))
            return null;
        long? size = long.TryParse(fields.GetValueOrDefault("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null;
        return new LinuxPackageInfo(fields.GetValueOrDefault("Version"), size, null);
    }

    /// <summary><c>pacman -Si &lt;pkg&gt;</c>.</summary>
    public static LinuxPackageInfo? ParsePacmanInfo(string output)
    {
        var fields = Fields(output, ':');
        if (!fields.ContainsKey("Name"))
            return null;
        return new LinuxPackageInfo(fields.GetValueOrDefault("Version"), ParseSize(fields.GetValueOrDefault("Download Size")), fields.GetValueOrDefault("Licenses"));
    }

    /// <summary><c>dnf info &lt;pkg&gt;</c> (dnf4 and dnf5 layouts).</summary>
    public static LinuxPackageInfo? ParseDnfInfo(string output)
    {
        var fields = Fields(output, ':');
        if (!fields.ContainsKey("Name"))
            return null;
        var size = fields.GetValueOrDefault("Download size") ?? fields.GetValueOrDefault("Size");
        return new LinuxPackageInfo(fields.GetValueOrDefault("Version"), ParseSize(size), fields.GetValueOrDefault("License"));
    }

    /// <summary>"32.1 MB", "512.0 kB", "1.2 GiB", "12 M" → bytes (SI for kB/MB/GB, binary for KiB/MiB/GiB and dnf's K/M/G).</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var match = SizeRegex().Match(text.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;
        var factor = match.Groups[2].Value switch
        {
            "" or "B" or "bytes" => 1d,
            "kB" or "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "K" or "k" or "KiB" => 1024d,
            "M" or "MiB" => 1024d * 1024,
            "G" or "GiB" => 1024d * 1024 * 1024,
            _ => double.NaN,
        };
        return double.IsNaN(factor) ? null : (long)Math.Round(value * factor);
    }

    /// <summary>Last percentage in a progress line such as "Installing 1/2… ████▍ 42%  1.2 MB/s".</summary>
    public static double? ParsePercent(string line)
    {
        var matches = PercentRegex().Matches(line);
        if (matches.Count == 0)
            return null;
        var value = int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        return value is >= 0 and <= 100 ? value : null;
    }

    private static Dictionary<string, string> Fields(string text, char separator)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(text))
        {
            var index = line.IndexOf(separator);
            if (index <= 0)
                continue;
            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            if (key.Length > 0 && !fields.ContainsKey(key))
                fields[key] = value;
        }
        return fields;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]*(\.[A-Za-z0-9_-]+){2,}$")]
    private static partial Regex FlatpakIdRegex();

    // Flatpak separates number and unit with a no-break space, printed as '?' under LC_ALL=C.
    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)[^0-9A-Za-z]*([A-Za-z]*)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}

/// <summary>The command-line tools DeployNest runs on Linux.</summary>
public enum LinuxTool
{
    Flatpak,
    Apt,
    Dnf,
    Pacman,
}

/// <summary>Maps package-manager exit codes and messages to <see cref="FailureKind"/>.</summary>
public static class LinuxFailureClassifier
{
    /// <summary>pkexec: the authentication dialog was dismissed.</summary>
    public const int PkexecDismissed = 126;

    /// <summary>pkexec: not authorized (wrong password or policy).</summary>
    public const int PkexecNotAuthorized = 127;

    public static LinuxTool ToolFor(LinuxPackageManager manager) => manager switch
    {
        LinuxPackageManager.Apt => LinuxTool.Apt,
        LinuxPackageManager.Dnf => LinuxTool.Dnf,
        _ => LinuxTool.Pacman,
    };

    public static FailureKind Classify(LinuxTool tool, int exitCode, string output)
    {
        if (exitCode == 0)
            return FailureKind.None;

        var text = output;
        bool Has(string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (Has("No space left on device") || Has("not enough free space") || Has("You don't have enough free space"))
            return FailureKind.DiskFull;
        if (Has("Could not resolve") || Has("Temporary failure resolving") || Has("Name or service not known") ||
            Has("Network is unreachable") || Has("Could not connect") || Has("Failed to connect"))
            return FailureKind.NetworkUnavailable;

        switch (tool)
        {
            case LinuxTool.Flatpak:
                if (Has("No remote refs found") || Has("Nothing matches") || Has("No such ref") || Has("not found in remote"))
                    return FailureKind.PackageNotFound;
                if (Has("already installed"))
                    return FailureKind.AlreadyInstalled;
                if (Has("architecture") && Has("not supported"))
                    return FailureKind.NotApplicable;
                if (Has("Not authorized") || Has("Authentication is required"))
                    return FailureKind.ElevationDenied;
                if (Has("Unable to load summary") || Has("While downloading") || Has("Server returned status"))
                    return FailureKind.DownloadFailed;
                break;
            case LinuxTool.Apt:
                if (Has("Unable to locate package") || Has("has no installation candidate"))
                    return FailureKind.PackageNotFound;
                if (Has("Could not get lock") || Has("Unable to acquire the dpkg frontend lock") || Has("is held by process"))
                    return FailureKind.InstallInProgress;
                if (Has("unmet dependencies") || Has("held broken packages"))
                    return FailureKind.DependencyFailed;
                if (Has("Failed to fetch"))
                    return FailureKind.DownloadFailed;
                break;
            case LinuxTool.Dnf:
                if (Has("No match for argument") || Has("No package") && Has("available"))
                    return FailureKind.PackageNotFound;
                if (Has("Problem:") || Has("conflicting requests") || Has("nothing provides"))
                    return FailureKind.DependencyFailed;
                if (Has("Cannot download") || Has("Curl error") || Has("Failed to download"))
                    return FailureKind.DownloadFailed;
                break;
            case LinuxTool.Pacman:
                if (Has("target not found"))
                    return FailureKind.PackageNotFound;
                if (Has("unable to lock database"))
                    return FailureKind.InstallInProgress;
                if (Has("could not satisfy dependencies") || Has("conflicting dependencies") || Has("are in conflict"))
                    return FailureKind.DependencyFailed;
                if (Has("failed to retrieve some files"))
                    return FailureKind.DownloadFailed;
                break;
        }

        return FailureKind.InstallerFailed;
    }
}
