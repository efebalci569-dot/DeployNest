namespace DeployNest.Core.Catalog;

/// <summary>A text value with per-language variants. Turkish ("tr") is the fallback language.</summary>
public sealed class LocalizedText
{
    public const string FallbackLanguage = "tr";

    private readonly IReadOnlyDictionary<string, string> _values;

    public LocalizedText(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public static LocalizedText Of(string turkish) =>
        new(new Dictionary<string, string> { [FallbackLanguage] = turkish });

    public IEnumerable<string> Languages => _values.Keys;

    public string Get(string language)
    {
        if (_values.TryGetValue(language, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        if (_values.TryGetValue(FallbackLanguage, out var fallback) && !string.IsNullOrWhiteSpace(fallback))
            return fallback;
        return _values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    public override string ToString() => Get(FallbackLanguage);
}
