using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeployNest.Core.Installation;

namespace DeployNest.Core.Winget;

/// <summary>Package metadata from "winget show". Fields are null when they could not be read.</summary>
public sealed record WingetPackageInfo(
    string Id,
    string? Name,
    string? Version,
    string? Publisher,
    string? License,
    string? LicenseUrl,
    string? Homepage,
    string? InstallerType,
    string? InstallerUrl,
    string? InstallerSha256,
    IReadOnlyList<string> Agreements,
    IReadOnlyList<string> Dependencies);

public static partial class WingetShowParser
{
    /// <summary>
    /// Parses "winget show --id X --exact" output. The package identifier is taken from the
    /// language-independent "[Id]" header; labelled fields are read only from English output,
    /// so on localized systems they are reported as unknown instead of being guessed.
    /// </summary>
    public static WingetPackageInfo? Parse(string output)
    {
        var lines = CleanLines(output);
        var headerIndex = lines.FindIndex(l => HeaderRegex().IsMatch(l));
        if (headerIndex < 0)
            return null;

        var header = HeaderRegex().Match(lines[headerIndex]);
        var id = header.Groups["id"].Value;
        var name = header.Groups["name"].Value.Trim();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var agreements = new List<string>();
        var dependencies = new List<string>();
        string? section = null;

        foreach (var raw in lines.Skip(headerIndex + 1))
        {
            if (raw.Length == 0)
                continue;

            var indent = raw.Length - raw.TrimStart().Length;
            var line = raw.Trim();
            var kv = FieldRegex().Match(line);

            if (indent == 0)
            {
                section = null;
                if (!kv.Success)
                    continue;
                var key = kv.Groups["key"].Value.Trim();
                var value = kv.Groups["value"].Value.Trim();
                if (value.Length == 0)
                    section = key;
                else
                    fields.TryAdd(key, value);
                continue;
            }

            if (string.Equals(section, "Installer", StringComparison.OrdinalIgnoreCase) && kv.Success)
            {
                var key = kv.Groups["key"].Value.Trim();
                var value = kv.Groups["value"].Value.Trim();
                if (string.Equals(key, "Dependencies", StringComparison.OrdinalIgnoreCase))
                {
                    section = "Installer.Dependencies";
                    continue;
                }
                fields.TryAdd("Installer " + key.Replace("Installer ", string.Empty, StringComparison.OrdinalIgnoreCase), value);
            }
            else if (string.Equals(section, "Agreements", StringComparison.OrdinalIgnoreCase))
            {
                agreements.Add(line);
            }
            else if (string.Equals(section, "Installer.Dependencies", StringComparison.OrdinalIgnoreCase) && !line.EndsWith(':'))
            {
                dependencies.Add(line.TrimStart('-', ' '));
            }
        }

        return new WingetPackageInfo(
            id,
            name.Length > 0 ? name : null,
            Get(fields, "Version"),
            Get(fields, "Publisher"),
            Get(fields, "License"),
            Get(fields, "License Url"),
            Get(fields, "Homepage"),
            Get(fields, "Installer Type"),
            Get(fields, "Installer Url"),
            Get(fields, "Installer SHA256"),
            agreements,
            dependencies);
    }

    private static string? Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>Splits output into lines, applying carriage returns and dropping spinner artifacts.</summary>
    internal static List<string> CleanLines(string output)
    {
        var result = new List<string>();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine;
            var cr = line.LastIndexOf('\r');
            if (cr >= 0)
                line = line[(cr + 1)..];
            line = line.Replace("\b", string.Empty, StringComparison.Ordinal).TrimEnd();
            if (line.Trim() is "-" or "\\" or "|" or "/")
                continue;
            result.Add(line);
        }
        return result;
    }

    [GeneratedRegex(@"^\S.*?\s(?<name>.*?)\s*\[(?<id>[A-Za-z0-9][A-Za-z0-9\-_.+]*)\]\s*$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^(?<key>[^:]+?):(\s+(?<value>.*))?$")]
    private static partial Regex FieldRegex();
}

public static class WingetExportParser
{
    /// <summary>Reads package identifiers from a "winget export" JSON document.</summary>
    public static IReadOnlySet<string> Parse(string json)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("Sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var source in sources.EnumerateArray())
        {
            if (!source.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var package in packages.EnumerateArray())
            {
                if (package.TryGetProperty("PackageIdentifier", out var id) && id.GetString() is { Length: > 0 } value)
                    ids.Add(value);
            }
        }
        return ids;
    }
}

/// <summary>
/// Converts the lines printed by "winget install" into progress phases. When winget's output is
/// redirected it prints only stage lines (no progress bar), so phases are derived from the line
/// sequence: a line with the installer URL means downloading, the following line means the
/// download was verified, and later lines mean the installer is running. The final result is
/// always taken from the exit code, never from this text.
/// </summary>
public sealed partial class WingetProgressInterpreter
{
    private ItemPhase _phase = ItemPhase.Preparing;
    private int _linesAfterDownload;

    public ItemPhase Phase => _phase;
    public string? DownloadHost { get; private set; }

    /// <summary>Returns a new progress value when the line changes what should be shown.</summary>
    public ItemProgress? OnLine(string rawLine)
    {
        var line = WingetShowParser.CleanLines(rawLine).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(line))
            return null;

        var bytes = BytesRegex().Match(line);
        if (bytes.Success)
        {
            var received = ParseSize(bytes.Groups["got"].Value, bytes.Groups["gotUnit"].Value);
            var total = ParseSize(bytes.Groups["total"].Value, bytes.Groups["totalUnit"].Value);
            _phase = ItemPhase.Downloading;
            var percent = total > 0 ? Math.Clamp(received * 100.0 / total, 0, 100) : (double?)null;
            return new ItemProgress(ItemPhase.Downloading, percent, received, total, DownloadHost);
        }

        var percentMatch = PercentRegex().Match(line);
        if (percentMatch.Success && _phase is ItemPhase.Downloading or ItemPhase.Preparing)
        {
            var percent = double.Parse(percentMatch.Groups["p"].Value, CultureInfo.InvariantCulture);
            _phase = ItemPhase.Downloading;
            return new ItemProgress(ItemPhase.Downloading, Math.Clamp(percent, 0, 100), Detail: DownloadHost);
        }

        var url = UrlRegex().Match(line);
        if (url.Success)
        {
            // Dependencies (e.g. a VC++ runtime) print their own download line, so a URL may
            // legitimately appear again after an earlier install step.
            DownloadHost = Uri.TryCreate(url.Value, UriKind.Absolute, out var uri) ? uri.Host : null;
            _phase = ItemPhase.Downloading;
            _linesAfterDownload = 0;
            return new ItemProgress(ItemPhase.Downloading, Detail: DownloadHost);
        }

        if (_phase == ItemPhase.Downloading)
        {
            _linesAfterDownload++;
            _phase = _linesAfterDownload == 1 && line.Contains("hash", StringComparison.OrdinalIgnoreCase)
                ? ItemPhase.Verifying
                : ItemPhase.Installing;
            return new ItemProgress(_phase);
        }

        if (_phase == ItemPhase.Verifying)
        {
            _phase = ItemPhase.Installing;
            return new ItemProgress(ItemPhase.Installing);
        }

        return null;
    }

    private static long ParseSize(string number, string unit)
    {
        var value = double.Parse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture);
        var multiplier = unit.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            _ => 1d,
        };
        return (long)(value * multiplier);
    }

    [GeneratedRegex(@"(?<got>\d+(?:[.,]\d+)?)\s*(?<gotUnit>KB|MB|GB|B)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>KB|MB|GB|B)", RegexOptions.IgnoreCase)]
    private static partial Regex BytesRegex();

    [GeneratedRegex(@"(?<p>\d{1,3})\s?%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
