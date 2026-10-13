using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DeployNest.Core.Winget;

namespace DeployNest.Core.Catalog;

public sealed record CatalogIssue(string Path, string Message, bool IsFatal);

public sealed record CatalogLoadResult(SoftwareCatalog? Catalog, IReadOnlyList<CatalogIssue> Issues)
{
    public bool IsUsable => Catalog is not null;
    public int RejectedEntryCount => Issues.Count(i => !i.IsFatal);
}

/// <summary>
/// Parses and validates the JSON catalog. Structural problems make the whole catalog unusable;
/// problems inside a single software entry or preset only reject that entry.
/// </summary>
public static partial class CatalogParser
{
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static CatalogLoadResult Parse(string json)
    {
        CatalogDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<CatalogDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Fatal("$", $"Katalog geçerli bir JSON değil: {ex.Message}");
        }

        if (dto is null)
            return Fatal("$", "Katalog boş.");
        if (dto.SchemaVersion != SupportedSchemaVersion)
            return Fatal("schemaVersion", $"Desteklenmeyen şema sürümü: {dto.SchemaVersion}. Beklenen: {SupportedSchemaVersion}.");

        var issues = new List<CatalogIssue>();
        var categories = ParseCategories(dto.Categories, issues);
        if (categories.Count == 0)
        {
            issues.Add(new CatalogIssue("categories", "Katalogda geçerli kategori yok.", true));
            return new CatalogLoadResult(null, issues);
        }

        var software = ParseSoftware(dto.Software, categories, issues);
        if (software.Count == 0)
        {
            issues.Add(new CatalogIssue("software", "Katalogda geçerli program yok.", true));
            return new CatalogLoadResult(null, issues);
        }

        var presets = ParsePresets(dto.Presets, software, issues);
        var catalog = new SoftwareCatalog(dto.SchemaVersion, dto.VerifiedOn, categories, software, presets);
        return new CatalogLoadResult(catalog, issues);
    }

    private static CatalogLoadResult Fatal(string path, string message) =>
        new(null, [new CatalogIssue(path, message, true)]);

    private static List<CatalogCategory> ParseCategories(List<CategoryDto?>? dtos, List<CatalogIssue> issues)
    {
        var result = new List<CatalogCategory>();
        if (dtos is null)
            return result;

        for (var i = 0; i < dtos.Count; i++)
        {
            var path = $"categories[{i}]";
            var dto = dtos[i];
            var errors = new List<string>();
            if (dto is null)
            {
                issues.Add(new CatalogIssue(path, "Kategori kaydı boş.", false));
                continue;
            }

            if (!IsValidId(dto.Id)) errors.Add("'id' küçük harf, rakam ve tire içermelidir.");
            else if (result.Any(c => c.Id == dto.Id)) errors.Add($"'{dto.Id}' kimliği tekrar ediyor.");
            var name = ToLocalized(dto.Name);
            if (name is null) errors.Add("'name.tr' zorunludur.");
            var icon = ParseGlyph(dto.Icon) ?? "";

            if (errors.Count > 0)
            {
                issues.Add(new CatalogIssue(path, string.Join(" ", errors), false));
                continue;
            }

            result.Add(new CatalogCategory(dto.Id!, name!, icon, result.Count));
        }

        return result;
    }

    private static List<SoftwareEntry> ParseSoftware(List<SoftwareDto?>? dtos, List<CatalogCategory> categories, List<CatalogIssue> issues)
    {
        var result = new List<SoftwareEntry>();
        if (dtos is null)
            return result;

        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var path = dto?.Id is { Length: > 0 } id ? $"software[{i}] ({id})" : $"software[{i}]";
            if (dto is null)
            {
                issues.Add(new CatalogIssue(path, "Program kaydı boş.", false));
                continue;
            }

            var errors = new List<string>();
            var entry = TryBuildSoftware(dto, categories, result, errors, result.Count);
            if (entry is null || errors.Count > 0)
            {
                issues.Add(new CatalogIssue(path, string.Join(" ", errors), false));
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private static SoftwareEntry? TryBuildSoftware(
        SoftwareDto dto,
        List<CatalogCategory> categories,
        List<SoftwareEntry> accepted,
        List<string> errors,
        int order)
    {
        if (!IsValidId(dto.Id)) errors.Add("'id' küçük harf, rakam ve tire içermelidir.");
        else if (accepted.Any(s => s.Id == dto.Id)) errors.Add($"'{dto.Id}' kimliği tekrar ediyor.");
        if (string.IsNullOrWhiteSpace(dto.Name)) errors.Add("'name' zorunludur.");
        if (string.IsNullOrWhiteSpace(dto.Publisher)) errors.Add("'publisher' zorunludur.");
        if (dto.Category is null || categories.All(c => c.Id != dto.Category))
            errors.Add($"'category' bilinen bir kategori olmalıdır ('{dto.Category}').");

        var summary = ToLocalized(dto.Summary);
        if (summary is null) errors.Add("'summary.tr' zorunludur.");
        var description = ToLocalized(dto.Description) ?? summary;
        var license = ToLocalized(dto.License) ?? LocalizedText.Of("Bilinmiyor");

        var homepage = ParseOptionalHttpsUri(dto.Homepage, "homepage", errors);
        var licenseUrl = ParseOptionalHttpsUri(dto.LicenseUrl, "licenseUrl", errors);

        var icon = new IconSpec(
            string.IsNullOrWhiteSpace(dto.Icon?.Monogram) ? FirstLetter(dto.Name) : dto.Icon!.Monogram!.Trim(),
            dto.Icon?.Color is { } color && HexColorRegex().IsMatch(color) ? color : "#5A6472");
        if (icon.Monogram.Length > 4) errors.Add("'icon.monogram' en fazla 4 karakter olabilir.");

        var elevation = dto.Elevation?.ToLowerInvariant() switch
        {
            null or "unknown" => ElevationHint.Unknown,
            "likely" => ElevationHint.Likely,
            "unlikely" => ElevationHint.Unlikely,
            var other => AddError<ElevationHint>(errors, $"'elevation' geçersiz: '{other}'."),
        };

        var winget = ParseWinget(dto.Winget, errors);
        var direct = ParseDirect(dto.Direct, errors);
        var linux = ParseLinux(dto.Linux, errors);
        if (dto.Winget is null && dto.Direct is null && dto.Linux is null)
            errors.Add("En az bir kurulum yöntemi ('winget', 'direct' veya 'linux') tanımlanmalıdır.");

        var detection = ParseDetection(dto.Detection, errors);

        if (errors.Count > 0)
            return null;

        return new SoftwareEntry(
            dto.Id!,
            dto.Name!.Trim(),
            dto.Publisher!.Trim(),
            dto.Category!,
            summary!,
            description!,
            homepage,
            license,
            licenseUrl,
            icon,
            (dto.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToArray(),
            elevation,
            ToLocalized(dto.Notes),
            winget,
            direct,
            detection,
            order,
            linux);
    }

    private static WingetSource? ParseWinget(WingetDto? dto, List<string> errors)
    {
        if (dto is null)
            return null;
        if (!WingetPackageId.IsValid(dto.Id))
        {
            errors.Add($"'winget.id' geçerli bir winget paket kimliği değil ('{dto.Id}').");
            return null;
        }
        if (string.IsNullOrWhiteSpace(dto.Publisher))
        {
            errors.Add("'winget.publisher' zorunludur; paket doğrulamasında yayıncı karşılaştırması için kullanılır.");
            return null;
        }
        return new WingetSource(dto.Id!, dto.Publisher.Trim());
    }

    private static DirectSource? ParseDirect(DirectDto? dto, List<string> errors)
    {
        if (dto is null)
            return null;

        var before = errors.Count;
        if (!Uri.TryCreate(dto.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            errors.Add("'direct.url' mutlak bir HTTPS adresi olmalıdır.");

        var hosts = (dto.AllowedHosts ?? []).Select(h => h?.Trim().ToLowerInvariant()).ToList();
        if (hosts.Count == 0 || hosts.Any(h => string.IsNullOrEmpty(h) || Uri.CheckHostName(h) != UriHostNameType.Dns))
            errors.Add("'direct.allowedHosts' geçerli alan adları içermelidir.");
        else if (url is not null && !hosts.Contains(url.Host.ToLowerInvariant()))
            errors.Add("'direct.url' alan adı 'allowedHosts' listesinde olmalıdır.");

        var type = dto.InstallerType?.ToLowerInvariant() switch
        {
            "msi" => InstallerType.Msi,
            "exe" => InstallerType.Exe,
            _ => AddError<InstallerType>(errors, "'direct.installerType' 'msi' veya 'exe' olmalıdır."),
        };

        string? sha = null;
        if (dto.Sha256 is not null)
        {
            if (Sha256Regex().IsMatch(dto.Sha256)) sha = dto.Sha256.ToLowerInvariant();
            else errors.Add("'direct.sha256' 64 karakterlik onaltılık bir değer olmalıdır.");
        }

        var signer = string.IsNullOrWhiteSpace(dto.Signer) ? null : dto.Signer.Trim();
        if (sha is null && signer is null)
            errors.Add("Doğrudan indirme için 'sha256' veya 'signer' doğrulamalarından en az biri zorunludur.");

        var args = dto.SilentArgs ?? [];
        Uri? argsSource = null;
        if (args.Count > 0)
        {
            if (type != InstallerType.Exe)
                errors.Add("'direct.silentArgs' yalnızca EXE kurulumlarında kullanılabilir; MSI için standart anahtarlar kullanılır.");
            if (!Uri.TryCreate(dto.SilentArgsSource, UriKind.Absolute, out argsSource) || argsSource.Scheme != Uri.UriSchemeHttps)
                errors.Add("'direct.silentArgs' için yayıncı belgesini gösteren 'silentArgsSource' (HTTPS) zorunludur.");
            if (args.Any(a => string.IsNullOrWhiteSpace(a) || a.Any(c => char.IsControl(c) || c is '"' or '&' or '|' or '<' or '>' or '^')))
                errors.Add("'direct.silentArgs' kontrol karakteri veya kabuk özel karakteri içeremez.");
        }

        if (dto.SizeBytes is < 0)
            errors.Add("'direct.sizeBytes' negatif olamaz.");

        if (errors.Count > before)
            return null;

        return new DirectSource(url!, hosts.Select(h => h!).ToArray(), type, dto.Version, sha, signer, dto.SizeBytes, args, argsSource);
    }

    private static LinuxSource? ParseLinux(LinuxDto? dto, List<string> errors)
    {
        if (dto is null)
            return null;

        var before = errors.Count;
        string? flatpak = null;
        if (dto.Flatpak is not null)
        {
            if (FlatpakIdRegex().IsMatch(dto.Flatpak)) flatpak = dto.Flatpak;
            else errors.Add($"'linux.flatpak' geçerli bir Flatpak uygulama kimliği değil ('{dto.Flatpak}').");
        }

        IReadOnlyList<string> Packages(List<string?>? names, string field)
        {
            if (names is null)
                return [];
            if (names.Count == 0 || names.Any(n => n is null || !LinuxPackageRegex().IsMatch(n)))
            {
                errors.Add($"'linux.{field}' geçerli paket adları içermelidir (harf/rakamla başlar; yalnızca harf, rakam, '+', '.', '_', '-').");
                return [];
            }
            return names.Distinct().ToArray()!;
        }

        var apt = Packages(dto.Apt, "apt");
        var dnf = Packages(dto.Dnf, "dnf");
        var pacman = Packages(dto.Pacman, "pacman");
        if (dto.Flatpak is null && dto.Apt is null && dto.Dnf is null && dto.Pacman is null)
            errors.Add("'linux' bölümünde 'flatpak' veya en az bir paket listesi ('apt', 'dnf', 'pacman') olmalıdır.");

        if (errors.Count > before)
            return null;

        return new LinuxSource(flatpak, apt, dnf, pacman, ToLocalized(dto.Notes));
    }

    private static DetectionRules ParseDetection(DetectionDto? dto, List<string> errors)
    {
        if (dto is null)
            return DetectionRules.Empty;

        var rules = new List<RegistryRule>();
        foreach (var rule in dto.Registry ?? [])
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.DisplayName))
            {
                errors.Add("'detection.registry' kurallarında 'displayName' zorunludur.");
                continue;
            }

            var displayName = TryRegex(rule.DisplayName, errors);
            var publisher = string.IsNullOrWhiteSpace(rule.Publisher) ? null : TryRegex(rule.Publisher, errors);
            if (displayName is not null)
                rules.Add(new RegistryRule(displayName, publisher));
        }

        var wingetIds = new List<string>();
        foreach (var id in dto.WingetIds ?? [])
        {
            if (WingetPackageId.IsValid(id)) wingetIds.Add(id!);
            else errors.Add($"'detection.wingetIds' geçersiz kimlik içeriyor ('{id}').");
        }

        return new DetectionRules(rules, wingetIds);
    }

    private static List<CatalogPreset> ParsePresets(List<PresetDto?>? dtos, List<SoftwareEntry> software, List<CatalogIssue> issues)
    {
        var result = new List<CatalogPreset>();
        if (dtos is null)
            return result;

        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var path = $"presets[{i}]";
            var name = ToLocalized(dto?.Name);
            if (dto is null || !IsValidId(dto.Id) || name is null || result.Any(p => p.Id == dto.Id))
            {
                issues.Add(new CatalogIssue(path, "Hazır seçim grubunda geçerli ve benzersiz 'id' ile 'name.tr' zorunludur.", false));
                continue;
            }

            var ids = (dto.Software ?? []).Distinct().ToList();
            var missing = ids.Where(id => software.All(s => s.Id != id)).ToList();
            if (missing.Count > 0)
                issues.Add(new CatalogIssue($"{path} ({dto.Id})", $"Bilinmeyen programlar gruptan çıkarıldı: {string.Join(", ", missing)}.", false));

            var known = ids.Except(missing).ToArray();
            if (known.Length == 0)
                continue;

            List<CatalogPlatform>? platforms = null;
            if (dto.Platforms is not null)
            {
                platforms = [];
                foreach (var value in dto.Platforms)
                {
                    if (Enum.TryParse<CatalogPlatform>(value, ignoreCase: true, out var platform) && Enum.IsDefined(platform))
                        platforms.Add(platform);
                    else
                        issues.Add(new CatalogIssue($"{path} ({dto.Id})", $"Bilinmeyen platform yok sayıldı: '{value}' ('windows' veya 'linux' olmalı).", false));
                }
            }

            result.Add(new CatalogPreset(dto.Id!, name, ToLocalized(dto.Description) ?? LocalizedText.Of(string.Empty), ParseGlyph(dto.Icon) ?? "", known!, platforms));
        }

        return result;
    }

    private static Regex? TryRegex(string pattern, List<string> errors)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException ex)
        {
            errors.Add($"Geçersiz düzenli ifade '{pattern}': {ex.Message}");
            return null;
        }
    }

    private static Uri? ParseOptionalHttpsUri(string? value, string field, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            return uri;
        errors.Add($"'{field}' mutlak bir HTTPS adresi olmalıdır.");
        return null;
    }

    private static LocalizedText? ToLocalized(Dictionary<string, string?>? values)
    {
        if (values is null || !values.TryGetValue(LocalizedText.FallbackLanguage, out var tr) || string.IsNullOrWhiteSpace(tr))
            return null;
        return new LocalizedText(values
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value!.Trim()));
    }

    private static string? ParseGlyph(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex) ||
            !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) ||
            code is < 0xE000 or > 0xF8FF)
            return null;
        return char.ConvertFromUtf32(code);
    }

    private static string FirstLetter(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

    private static bool IsValidId(string? id) => id is not null && IdRegex().IsMatch(id);

    private static T AddError<T>(List<string> errors, string message)
    {
        errors.Add(message);
        return default!;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColorRegex();

    /// <summary>Reverse-DNS application id with at least three parts (e.g. org.videolan.VLC).</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*(\\.[A-Za-z0-9_-]+){2,}$")]
    private static partial Regex FlatpakIdRegex();

    /// <summary>Package names never start with '-' so they cannot be read as command options.</summary>
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+._-]{0,99}$")]
    private static partial Regex LinuxPackageRegex();

    private sealed class CatalogDto
    {
        public int SchemaVersion { get; set; }
        public string? VerifiedOn { get; set; }
        public List<CategoryDto?>? Categories { get; set; }
        public List<SoftwareDto?>? Software { get; set; }
        public List<PresetDto?>? Presets { get; set; }
    }

    private sealed class CategoryDto
    {
        public string? Id { get; set; }
        public Dictionary<string, string?>? Name { get; set; }
        public string? Icon { get; set; }
    }

    private sealed class SoftwareDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Publisher { get; set; }
        public string? Category { get; set; }
        public Dictionary<string, string?>? Summary { get; set; }
        public Dictionary<string, string?>? Description { get; set; }
        public string? Homepage { get; set; }
        public Dictionary<string, string?>? License { get; set; }
        public string? LicenseUrl { get; set; }
        public IconDto? Icon { get; set; }
        public List<string>? Tags { get; set; }
        public string? Elevation { get; set; }
        public Dictionary<string, string?>? Notes { get; set; }
        public WingetDto? Winget { get; set; }
        public DirectDto? Direct { get; set; }
        public DetectionDto? Detection { get; set; }
        public LinuxDto? Linux { get; set; }
    }

    private sealed class LinuxDto
    {
        public string? Flatpak { get; set; }
        public List<string?>? Apt { get; set; }
        public List<string?>? Dnf { get; set; }
        public List<string?>? Pacman { get; set; }
        public Dictionary<string, string?>? Notes { get; set; }
    }

    private sealed class IconDto
    {
        public string? Monogram { get; set; }
        public string? Color { get; set; }
    }

    private sealed class WingetDto
    {
        public string? Id { get; set; }
        public string? Publisher { get; set; }
    }

    private sealed class DirectDto
    {
        public string? Url { get; set; }
        public List<string?>? AllowedHosts { get; set; }
        public string? InstallerType { get; set; }
        public string? Version { get; set; }
        public string? Sha256 { get; set; }
        public string? Signer { get; set; }
        public long? SizeBytes { get; set; }
        public List<string>? SilentArgs { get; set; }
        public string? SilentArgsSource { get; set; }
    }

    private sealed class DetectionDto
    {
        public List<RegistryRuleDto?>? Registry { get; set; }
        public List<string?>? WingetIds { get; set; }
    }

    private sealed class RegistryRuleDto
    {
        public string? DisplayName { get; set; }
        public string? Publisher { get; set; }
    }

    private sealed class PresetDto
    {
        public string? Id { get; set; }
        public Dictionary<string, string?>? Name { get; set; }
        public Dictionary<string, string?>? Description { get; set; }
        public string? Icon { get; set; }
        public List<string>? Software { get; set; }
        public List<string?>? Platforms { get; set; }
    }
}
