using DeployNest.Core.Detection;

namespace DeployNest.Core.Catalog;

public enum InstallStateFilter
{
    All,
    Installed,
    NotInstalled,
    Selected,
}

public sealed record CatalogFilter(string? Query = null, string? CategoryId = null, InstallStateFilter State = InstallStateFilter.All);

/// <summary>Search and filter logic shared by the UI and the tests.</summary>
public static class CatalogQuery
{
    /// <summary>
    /// Filters <paramref name="items"/> and orders them by relevance (when a query is given)
    /// and then by catalog order.
    /// </summary>
    public static IReadOnlyList<T> Apply<T>(
        IEnumerable<T> items,
        CatalogFilter filter,
        Func<T, SoftwareEntry> entryOf,
        Func<T, InstallState> stateOf,
        Func<T, bool> isSelected,
        Func<string, string>? categoryNameOf = null)
    {
        var tokens = TextNormalizer.Tokenize(filter.Query);

        var matches = new List<(T Item, int Score, int Order)>();
        foreach (var item in items)
        {
            var entry = entryOf(item);
            if (filter.CategoryId is not null && entry.CategoryId != filter.CategoryId)
                continue;

            var include = filter.State switch
            {
                InstallStateFilter.Installed => stateOf(item) == InstallState.Installed,
                InstallStateFilter.NotInstalled => stateOf(item) == InstallState.NotInstalled,
                InstallStateFilter.Selected => isSelected(item),
                _ => true,
            };
            if (!include)
                continue;

            var score = tokens.Length == 0 ? 0 : Score(entry, tokens, categoryNameOf?.Invoke(entry.CategoryId));
            if (score < 0)
                continue;

            matches.Add((item, score, entry.Order));
        }

        return matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Order)
            .Select(m => m.Item)
            .ToList();
    }

    /// <summary>Returns -1 when an entry does not match every token; higher is more relevant.</summary>
    internal static int Score(SoftwareEntry entry, string[] tokens, string? categoryName)
    {
        var name = TextNormalizer.Normalize(entry.Name);
        var nameCompact = TextNormalizer.Compact(entry.Name);
        var secondary = TextNormalizer.Normalize(string.Join(' ',
            new[] { entry.Publisher, entry.Summary.ToString(), categoryName ?? string.Empty }.Concat(entry.Tags)));
        var secondaryCompact = TextNormalizer.Compact(secondary);

        var score = 0;
        foreach (var token in tokens)
        {
            var compactToken = TextNormalizer.Compact(token);
            if (name.StartsWith(token, StringComparison.Ordinal) || nameCompact.StartsWith(compactToken, StringComparison.Ordinal))
                score += 100;
            else if (name.Contains(token, StringComparison.Ordinal) || (compactToken.Length > 0 && nameCompact.Contains(compactToken, StringComparison.Ordinal)))
                score += 60;
            else if (secondary.Contains(token, StringComparison.Ordinal) || (compactToken.Length > 0 && secondaryCompact.Contains(compactToken, StringComparison.Ordinal)))
                score += 20;
            else
                return -1;
        }

        return score;
    }
}
