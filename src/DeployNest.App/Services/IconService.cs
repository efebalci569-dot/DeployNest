using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace DeployNest.App.Services;

/// <summary>
/// Loads the real icon of an installed program from its registry "DisplayIcon" value
/// ("C:\path\app.exe,0" or an .ico file). Only local files are read.
/// </summary>
public sealed partial class IconService(ILogger<IconService> logger)
{
    private const int IconSize = 64;
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public Task<ImageSource?> LoadAsync(string? displayIcon) =>
        string.IsNullOrWhiteSpace(displayIcon)
            ? Task.FromResult<ImageSource?>(null)
            : Task.Run(() => _cache.GetOrAdd(displayIcon, Load));

    private ImageSource? Load(string displayIcon)
    {
        var (path, index) = Parse(displayIcon);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".ico")
            {
                var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }

            if (extension is not (".exe" or ".dll"))
                return null;

            var icons = new IntPtr[1];
            var ids = new uint[1];
            var count = PrivateExtractIconsW(path, index, IconSize, IconSize, icons, ids, 1, 0);
            if (count == 0 || icons[0] == IntPtr.Zero)
                return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(icons[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or COMException or FileFormatException or ArgumentException)
        {
            logger.LogDebug(ex, "İkon okunamadı");
            return null;
        }
    }

    internal static (string? Path, int Index) Parse(string displayIcon)
    {
        var value = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        var index = 0;
        var comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out var parsed))
        {
            index = parsed;
            value = value[..comma];
        }
        value = value.Trim().Trim('"');
        return value.Length == 0 ? (null, 0) : (value, index);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);
}
