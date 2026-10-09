using DeployNest.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Catalog;

public sealed record LoadedCatalog(CatalogLoadResult Result, string Source, bool UsedFallback);

/// <summary>
/// Loads the program catalog. A "catalog\software.json" file next to the executable takes
/// precedence (so the catalog can be extended without rebuilding); otherwise the catalog that
/// is embedded in the application is used. An invalid external catalog never prevents startup.
/// </summary>
public sealed class CatalogProvider(ILogger<CatalogProvider> logger)
{
    public const string EmbeddedResourceName = "DeployNest.Catalog.software.json";

    public static string ExternalCatalogPath => Path.Combine(AppContext.BaseDirectory, "catalog", "software.json");

    public LoadedCatalog Load()
    {
        var external = ExternalCatalogPath;
        if (File.Exists(external))
        {
            try
            {
                var result = CatalogParser.Parse(File.ReadAllText(external));
                LogIssues(result, external);
                if (result.IsUsable)
                    return new LoadedCatalog(result, external, false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Harici katalog okunamadı");
            }

            var fallback = LoadEmbedded();
            return fallback with { UsedFallback = true };
        }

        return LoadEmbedded();
    }

    public LoadedCatalog LoadEmbedded()
    {
        var result = CatalogParser.Parse(ReadEmbeddedJson());
        LogIssues(result, "embedded");
        return new LoadedCatalog(result, "embedded", false);
    }

    public static string ReadEmbeddedJson()
    {
        using var stream = typeof(CatalogProvider).Assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException("Yerleşik katalog bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void LogIssues(CatalogLoadResult result, string source)
    {
        foreach (var issue in result.Issues)
            logger.LogWarning("Katalog sorunu ({Source}) {Path}: {Message}", source, issue.Path, issue.Message);
        if (result.Catalog is { } catalog)
            logger.LogInformation("Katalog yüklendi ({Source}): {Count} program", source, catalog.Software.Count);
    }
}
