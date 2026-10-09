using DeployNest.Core.Abstractions;
using DeployNest.Core.Catalog;
using DeployNest.Core.Installation;

namespace DeployNest.Tests;

internal static class TestData
{
    public const string MinimalCatalog = """
        {
          "schemaVersion": 1,
          "categories": [
            { "id": "browsers", "name": { "tr": "Tarayıcılar" }, "icon": "E774" },
            { "id": "tools", "name": { "tr": "Araçlar" } }
          ],
          "software": [
            {
              "id": "chrome", "name": "Google Chrome", "publisher": "Google LLC", "category": "browsers",
              "summary": { "tr": "Web tarayıcısı" },
              "tags": [ "tarayıcı", "internet" ],
              "winget": { "id": "Google.Chrome", "publisher": "Google LLC" },
              "detection": { "registry": [ { "displayName": "^Google Chrome$", "publisher": "^Google LLC$" } ] }
            },
            {
              "id": "seven-zip", "name": "7-Zip", "publisher": "Igor Pavlov", "category": "tools",
              "summary": { "tr": "Arşiv aracı, sıkıştırma" },
              "winget": { "id": "7zip.7zip", "publisher": "Igor Pavlov" },
              "direct": {
                "url": "https://www.7-zip.org/a/7z2604-x64.msi",
                "allowedHosts": [ "www.7-zip.org" ],
                "installerType": "msi",
                "sha256": "0b01334a418654293513449f61d6bfc99e5196ca371e3c3dc961eda57cd535c6",
                "sizeBytes": 2009088
              },
              "detection": { "registry": [ { "displayName": "^7-Zip \\d", "publisher": "^Igor Pavlov$" } ] }
            },
            {
              "id": "turkce-araci", "name": "Türkçe Şablon Aracı", "publisher": "Örnek Yayıncı", "category": "tools",
              "summary": { "tr": "Güzel çıktılar üretir" },
              "winget": { "id": "Ornek.Sablon", "publisher": "Örnek Yayıncı" }
            }
          ],
          "presets": [
            { "id": "basic", "name": { "tr": "Temel" }, "software": [ "chrome", "seven-zip", "missing-app" ] }
          ]
        }
        """;

    public static SoftwareCatalog Catalog()
    {
        var result = CatalogParser.Parse(MinimalCatalog);
        Assert.True(result.IsUsable, string.Join("; ", result.Issues.Select(i => i.Message)));
        return result.Catalog!;
    }

    public static PackageVerification Verified(long? size = null) =>
        new(true, FailureKind.None, "1.0", "Pub", null, "msi", size, null, null, [], [], null);
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(3));
}

/// <summary>Scriptable strategy: outcomes per software id, optional blocking for cancellation tests.</summary>
internal sealed class FakeStrategy(InstallMethod method, bool prefetch = false) : IInstallStrategy
{
    public Dictionary<string, Func<CancellationToken, Task<InstallOutcome>>> Behaviors { get; } = new();
    public List<string> InstallOrder { get; } = [];
    public List<string> Prepared { get; } = [];
    public List<string> CleanedUp { get; } = [];
    public InstallMethod Method => method;
    public bool SupportsPrefetch => prefetch;

    public Task<PreparedInstall> PrepareAsync(InstallRequest request, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Prepared)
            Prepared.Add(request.Software.Id);
        return Task.FromResult(PreparedInstall.Ready(request, "file-" + request.Software.Id));
    }

    public async Task<InstallOutcome> InstallAsync(PreparedInstall prepared, IProgress<ItemProgress> progress, CancellationToken cancellationToken)
    {
        var id = prepared.Request.Software.Id;
        InstallOrder.Add(id);
        progress.Report(new ItemProgress(ItemPhase.Installing));
        if (Behaviors.TryGetValue(id, out var behavior))
            return await behavior(cancellationToken);
        return InstallOutcome.Success();
    }

    public string? Cleanup(PreparedInstall prepared)
    {
        lock (CleanedUp)
            CleanedUp.Add(prepared.Request.Software.Id);
        return null;
    }
}
