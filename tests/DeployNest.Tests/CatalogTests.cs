using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Infrastructure.Catalog;

namespace DeployNest.Tests;

public class CatalogParserTests
{
    [Fact]
    public void Parses_valid_catalog_and_drops_unknown_preset_members()
    {
        var result = CatalogParser.Parse(TestData.MinimalCatalog);

        Assert.True(result.IsUsable);
        var catalog = result.Catalog!;
        Assert.Equal(2, catalog.Categories.Count);
        Assert.Equal(3, catalog.Software.Count);
        Assert.Equal(["chrome", "seven-zip"], catalog.Presets.Single().SoftwareIds);
        Assert.Contains(result.Issues, i => i.Message.Contains("missing-app") && !i.IsFatal);

        var sevenZip = catalog.FindSoftware("seven-zip")!;
        Assert.Equal(InstallerType.Msi, sevenZip.Direct!.InstallerType);
        Assert.True(sevenZip.Direct.RunsUnattended);
        Assert.Equal("", catalog.FindCategory("browsers")!.IconGlyph);
    }

    [Fact]
    public void Shipped_catalog_is_valid_and_complete()
    {
        var result = CatalogParser.Parse(CatalogProvider.ReadEmbeddedJson());

        Assert.True(result.IsUsable);
        Assert.Empty(result.Issues);
        var catalog = result.Catalog!;
        Assert.Equal(24, catalog.Software.Count);
        Assert.Equal(7, catalog.Categories.Count);
        Assert.All(catalog.Software, s => Assert.NotNull(s.Winget));
        Assert.All(catalog.Presets, p => Assert.NotEmpty(p.SoftwareIds));
        Assert.Contains(catalog.Presets, p => p.Id == "developer");
        Assert.Equal(catalog.Software.Count, catalog.Software.Select(s => s.Winget!.PackageId).Distinct().Count());
    }

    [Fact]
    public void Every_catalog_program_has_a_logo_and_the_repo_copy_matches_the_embedded_catalog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "catalog", "software.json")))
            root = root.Parent;
        Assert.NotNull(root);

        var catalog = CatalogParser.Parse(CatalogProvider.ReadEmbeddedJson()).Catalog!;
        foreach (var software in catalog.Software)
            Assert.True(File.Exists(Path.Combine(root.FullName, "assets", "icons", "apps", software.Id + ".png")), $"Logo eksik: {software.Id}");

        var websiteCatalog = Path.Combine(root.FullName, "website", "data", "catalog.json");
        if (File.Exists(websiteCatalog))
        {
            Assert.Equal(
                File.ReadAllText(Path.Combine(root.FullName, "catalog", "software.json")).ReplaceLineEndings(),
                File.ReadAllText(websiteCatalog).ReplaceLineEndings());
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "schemaVersion": 2, "categories": [], "software": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "categories": [], "software": [] }""")]
    [InlineData("null")]
    public void Structural_problems_make_catalog_unusable(string json)
    {
        var result = CatalogParser.Parse(json);

        Assert.False(result.IsUsable);
        Assert.Contains(result.Issues, i => i.IsFatal);
    }

    [Theory]
    [InlineData(""" "winget": { "id": "Google Chrome", "publisher": "Google LLC" } """, "winget.id")]
    [InlineData(""" "winget": { "id": "Google.Chrome;calc", "publisher": "Google LLC" } """, "winget.id")]
    [InlineData(""" "winget": { "id": "Google.Chrome" } """, "winget.publisher")]
    [InlineData(""" "direct": { "url": "http://example.com/a.msi", "allowedHosts": ["example.com"], "installerType": "msi", "signer": "X" } """, "HTTPS")]
    [InlineData(""" "direct": { "url": "https://evil.example/a.msi", "allowedHosts": ["example.com"], "installerType": "msi", "signer": "X" } """, "allowedHosts")]
    [InlineData(""" "direct": { "url": "https://example.com/a.msi", "allowedHosts": ["example.com"], "installerType": "msi" } """, "sha256")]
    [InlineData(""" "direct": { "url": "https://example.com/a.exe", "allowedHosts": ["example.com"], "installerType": "exe", "signer": "X", "silentArgs": ["/S"] } """, "silentArgsSource")]
    [InlineData(""" "direct": { "url": "https://example.com/a.exe", "allowedHosts": ["example.com"], "installerType": "exe", "signer": "X", "silentArgs": ["/S & calc"], "silentArgsSource": "https://example.com/docs" } """, "kabuk")]
    [InlineData(""" "winget": { "id": "A.B", "publisher": "P" }, "detection": { "registry": [ { "displayName": "([" } ] } """, "düzenli ifade")]
    [InlineData(""" "category": "nope", "winget": { "id": "A.B", "publisher": "P" } """, "category")]
    public void Invalid_entries_are_rejected_without_breaking_the_catalog(string fragment, string expectedMessagePart)
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "categories": [ { "id": "tools", "name": { "tr": "Araçlar" } } ],
              "software": [
                { "id": "good", "name": "Good", "publisher": "P", "category": "tools", "summary": { "tr": "ok" },
                  "winget": { "id": "Good.App", "publisher": "P" } },
                { "id": "bad", "name": "Bad", "publisher": "P", "summary": { "tr": "x" }, "category": "tools", {{fragment}} }
              ]
            }
            """;

        var result = CatalogParser.Parse(json);

        Assert.True(result.IsUsable);
        Assert.Equal(["good"], result.Catalog!.Software.Select(s => s.Id));
        var issue = Assert.Single(result.Issues);
        Assert.False(issue.IsFatal);
        Assert.Contains(expectedMessagePart, issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Duplicate_ids_and_missing_install_method_are_rejected()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "categories": [ { "id": "tools", "name": { "tr": "Araçlar" } } ],
              "software": [
                { "id": "app", "name": "A", "publisher": "P", "category": "tools", "summary": { "tr": "a" }, "winget": { "id": "A.A", "publisher": "P" } },
                { "id": "app", "name": "A2", "publisher": "P", "category": "tools", "summary": { "tr": "a" }, "winget": { "id": "A.B", "publisher": "P" } },
                { "id": "no-method", "name": "N", "publisher": "P", "category": "tools", "summary": { "tr": "n" } },
                { "id": "Upper", "name": "U", "publisher": "P", "category": "tools", "summary": { "tr": "u" }, "winget": { "id": "U.U", "publisher": "P" } }
              ]
            }
            """;

        var result = CatalogParser.Parse(json);

        Assert.Equal(["app"], result.Catalog!.Software.Select(s => s.Id));
        Assert.Equal(3, result.RejectedEntryCount);
    }

    [Fact]
    public void Localized_text_falls_back_to_turkish()
    {
        var text = new LocalizedText(new Dictionary<string, string> { ["tr"] = "Merhaba", ["en"] = "Hello" });

        Assert.Equal("Hello", text.Get("en"));
        Assert.Equal("Merhaba", text.Get("de"));
    }
}

public class CatalogQueryTests
{
    private static readonly SoftwareCatalog Catalog = TestData.Catalog();

    private static IReadOnlyList<string> Search(CatalogFilter filter, Func<SoftwareEntry, InstallState>? state = null, Func<SoftwareEntry, bool>? selected = null) =>
        CatalogQuery.Apply(
            Catalog.Software,
            filter,
            s => s,
            state ?? (_ => InstallState.NotInstalled),
            selected ?? (_ => false),
            id => Catalog.FindCategory(id)!.Name.ToString())
        .Select(s => s.Id)
        .ToList();

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("CHROME", "chrome")]
    [InlineData("  google   chrome ", "chrome")]
    [InlineData("7zip", "seven-zip")]
    [InlineData("7 zip", "seven-zip")]
    [InlineData("7-ZIP", "seven-zip")]
    [InlineData("SIKIŞTIRMA", "seven-zip")]
    [InlineData("sikistirma", "seven-zip")]
    [InlineData("turkce", "turkce-araci")]
    [InlineData("ŞABLON", "turkce-araci")]
    [InlineData("ornek yayinci", "turkce-araci")]
    [InlineData("tarayici", "chrome")]
    public void Search_is_case_and_turkish_character_tolerant(string query, string expected)
    {
        Assert.Equal([expected], Search(new CatalogFilter(query)));
    }

    [Fact]
    public void No_match_returns_empty()
    {
        Assert.Empty(Search(new CatalogFilter("photoshop")));
    }

    [Fact]
    public void Name_matches_rank_above_description_matches()
    {
        // "araci" is in the Turkish tool's name and only in 7-Zip's summary ("Arşiv aracı").
        Assert.Equal(["turkce-araci", "seven-zip"], Search(new CatalogFilter("araci")));
    }

    [Fact]
    public void Empty_query_keeps_catalog_order()
    {
        Assert.Equal(["chrome", "seven-zip", "turkce-araci"], Search(new CatalogFilter()));
    }

    [Fact]
    public void Category_filter()
    {
        Assert.Equal(["seven-zip", "turkce-araci"], Search(new CatalogFilter(CategoryId: "tools")));
        Assert.Equal(["chrome"], Search(new CatalogFilter("google", "browsers")));
        Assert.Empty(Search(new CatalogFilter("google", "tools")));
    }

    [Fact]
    public void State_filters()
    {
        InstallState State(SoftwareEntry s) => s.Id switch
        {
            "chrome" => InstallState.Installed,
            "seven-zip" => InstallState.NotInstalled,
            _ => InstallState.Unverifiable,
        };

        Assert.Equal(["chrome"], Search(new CatalogFilter(State: InstallStateFilter.Installed), State));
        Assert.Equal(["seven-zip"], Search(new CatalogFilter(State: InstallStateFilter.NotInstalled), State));
        Assert.Equal(["turkce-araci"], Search(new CatalogFilter(State: InstallStateFilter.Selected), State, s => s.Id == "turkce-araci"));
    }

    [Fact]
    public void Normalizer_handles_turkish_letters()
    {
        Assert.Equal("istanbul cagri sukru ogun ilik", TextNormalizer.Normalize("İSTANBUL  Çağrı Şükrü Öğün ılık"));
        Assert.Equal("7zip", TextNormalizer.Compact("7-Zip"));
    }
}
