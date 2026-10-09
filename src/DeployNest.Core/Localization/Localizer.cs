using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace DeployNest.Core.Localization;

public sealed record LanguageInfo(string Code, string DisplayName);

public interface ILocalizer
{
    string Language { get; }
    CultureInfo Culture { get; }
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }
    string this[string key] { get; }
    string Format(string key, params object?[] args);
    void SetLanguage(string code);
    event EventHandler? LanguageChanged;
}

/// <summary>
/// Loads UI strings from embedded JSON language files (Localization/Languages/&lt;code&gt;.json).
/// Adding a language only requires adding a new JSON file with the same keys.
/// Missing keys fall back to Turkish, then to the key itself so gaps are visible.
/// </summary>
public sealed class JsonLocalizer : ILocalizer
{
    public const string DefaultLanguage = "tr";
    private const string ResourcePrefix = "DeployNest.Core.Localization.";

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _languages;
    private IReadOnlyDictionary<string, string> _current;
    private readonly IReadOnlyDictionary<string, string> _fallback;

    public JsonLocalizer(string? language = null)
        : this(LoadEmbeddedLanguages(), language)
    {
    }

    internal JsonLocalizer(Dictionary<string, IReadOnlyDictionary<string, string>> languages, string? language)
    {
        if (!languages.ContainsKey(DefaultLanguage))
            throw new InvalidOperationException("Varsayılan dil dosyası (tr.json) bulunamadı.");

        _languages = languages;
        _fallback = languages[DefaultLanguage];
        AvailableLanguages = languages
            .Select(kv => new LanguageInfo(kv.Key, kv.Value.TryGetValue("_language", out var n) ? n : kv.Key))
            .OrderBy(l => l.Code == DefaultLanguage ? 0 : 1)
            .ThenBy(l => l.DisplayName, StringComparer.CurrentCulture)
            .ToList();
        Language = DefaultLanguage;
        Culture = CultureInfo.GetCultureInfo("tr-TR");
        _current = _fallback;
        if (language is not null)
            SetLanguage(language);
    }

    public string Language { get; private set; }
    public CultureInfo Culture { get; private set; }
    public IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    public event EventHandler? LanguageChanged;

    public string this[string key] =>
        _current.TryGetValue(key, out var value) ? value :
        _fallback.TryGetValue(key, out var fallback) ? fallback :
        key;

    public string Format(string key, params object?[] args) =>
        string.Format(Culture, this[key], args);

    public void SetLanguage(string code)
    {
        if (!_languages.TryGetValue(code, out var strings) || code == Language)
            return;

        _current = strings;
        Language = code;
        Culture = TryGetCulture(strings.TryGetValue("_culture", out var c) ? c : code);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    private static CultureInfo TryGetCulture(string name)
    {
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadEmbeddedLanguages()
    {
        var assembly = typeof(JsonLocalizer).Assembly;
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal))
                continue;

            var code = name[ResourcePrefix.Length..^".json".Length];
            using var stream = assembly.GetManifestResourceStream(name)!;
            var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                ?? throw new InvalidOperationException($"Dil dosyası okunamadı: {name}");
            result[code] = strings;
        }
        return result;
    }

    internal static Assembly ResourceAssembly => typeof(JsonLocalizer).Assembly;
}
