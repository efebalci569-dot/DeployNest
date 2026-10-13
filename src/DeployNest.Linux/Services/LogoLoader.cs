using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeployNest.Linux.Services;

/// <summary>Loads the program logos shipped with the app (assets/icons/apps/&lt;id&gt;.png).</summary>
public static class LogoLoader
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public static Bitmap? Load(string softwareId) => Cache.GetOrAdd(softwareId, static id =>
    {
        var uri = new Uri($"avares://DeployNest/Assets/Apps/{id}.png");
        if (!AssetLoader.Exists(uri))
            return null;
        using var stream = AssetLoader.Open(uri);
        return Bitmap.DecodeToWidth(stream, 96);
    });
}
