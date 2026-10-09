using System.Globalization;
using System.Text;

namespace DeployNest.Core.Catalog;

/// <summary>
/// Normalizes text for tolerant searching: case-insensitive, Turkish-aware (ı/İ/ş/ğ/ü/ö/ç)
/// and diacritic-insensitive, so "turkce" matches "Türkçe" and "ISTANBUL" matches "İstanbul".
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var mapped = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            mapped.Append(ch switch
            {
                'İ' or 'I' or 'ı' => 'i',
                _ => ch,
            });
        }

        var decomposed = mapped.ToString().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                    builder.Append(' ');
                lastWasSpace = true;
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Normalized text with every non letter/digit removed ("7-Zip" → "7zip").</summary>
    public static string Compact(string? text)
    {
        var normalized = Normalize(text);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }
        return builder.ToString();
    }

    public static string[] Tokenize(string? query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
