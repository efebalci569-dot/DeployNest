using DeployNest.Core.Catalog;

namespace DeployNest.Core.Linux;

/// <summary>Distribution identity read from /etc/os-release.</summary>
public sealed record LinuxDistribution(string Id, IReadOnlyList<string> IdLike, string PrettyName, string? VersionId)
{
    public static LinuxDistribution Unknown { get; } = new("linux", [], "Linux", null);

    /// <summary>Parses the KEY=value format of os-release(5); quoted values are unquoted.</summary>
    public static LinuxDistribution Parse(string osRelease)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in osRelease.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
                value = value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
            values[line[..separator]] = value;
        }

        var id = values.TryGetValue("ID", out var i) && i.Length > 0 ? i.ToLowerInvariant() : "linux";
        var like = values.TryGetValue("ID_LIKE", out var l)
            ? l.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];
        var pretty = values.TryGetValue("PRETTY_NAME", out var p) && p.Length > 0
            ? p
            : values.TryGetValue("NAME", out var n) && n.Length > 0 ? n : "Linux";
        return new LinuxDistribution(id, like, pretty, values.GetValueOrDefault("VERSION_ID"));
    }

    private IEnumerable<string> Family => IdLike.Prepend(Id);

    /// <summary>
    /// Picks the package manager of the distribution family. The family decides first (so a
    /// stray dnf binary on Ubuntu is ignored); the tool must also be present on the system.
    /// When the family is unknown, the first available tool is used.
    /// </summary>
    public LinuxPackageManager? ChoosePackageManager(Func<LinuxPackageManager, bool> isAvailable)
    {
        LinuxPackageManager? byFamily = null;
        foreach (var name in Family)
        {
            byFamily = name switch
            {
                "debian" or "ubuntu" or "linuxmint" or "pop" or "elementary" or "zorin" or "kali" or "raspbian" => LinuxPackageManager.Apt,
                "fedora" or "rhel" or "centos" or "rocky" or "almalinux" or "nobara" or "ultramarine" => LinuxPackageManager.Dnf,
                "arch" or "manjaro" or "endeavouros" or "garuda" or "cachyos" => LinuxPackageManager.Pacman,
                _ => null,
            };
            if (byFamily is not null)
                break;
        }

        if (byFamily is { } family)
            return isAvailable(family) ? family : null;

        foreach (var manager in Enum.GetValues<LinuxPackageManager>())
        {
            if (isAvailable(manager))
                return manager;
        }
        return null;
    }
}

/// <summary>What DeployNest found on this Linux system.</summary>
public sealed record LinuxSystemInfo(
    LinuxDistribution Distribution,
    LinuxPackageManager? PackageManager,
    bool FlatpakAvailable,
    string? FlatpakVersion,
    bool ElevationAvailable,
    string Architecture)
{
    public string PackageManagerName => PackageManager switch
    {
        LinuxPackageManager.Apt => "apt",
        LinuxPackageManager.Dnf => "dnf",
        LinuxPackageManager.Pacman => "pacman",
        _ => "-",
    };
}
