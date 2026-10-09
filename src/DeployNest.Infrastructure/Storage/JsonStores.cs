using System.Text.Json;
using System.Text.Json.Serialization;
using DeployNest.Core.Abstractions;
using DeployNest.Core.History;
using DeployNest.Core.Settings;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Storage;

/// <summary>
/// Small JSON file persistence with atomic replace (write temp file, then move over the target),
/// so a crash during saving never leaves a half-written file. A corrupt file is renamed to
/// "*.corrupt-*" and the default value is used instead, so the app always starts.
/// </summary>
public sealed class JsonFile<T>(string path, Func<T> createDefault, ILogger logger)
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public T Read()
    {
        lock (_lock)
        {
            if (!File.Exists(Path))
                return createDefault();

            try
            {
                var json = File.ReadAllText(Path);
                return JsonSerializer.Deserialize<T>(json, Options) ?? createDefault();
            }
            catch (JsonException ex)
            {
                var backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                logger.LogWarning(ex, "Bozuk dosya yedeklendi ve varsayılan değerler kullanıldı: {Backup}", backup);
                TryMove(Path, backup);
                return createDefault();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Dosya okunamadı: {Path}", Path);
                return createDefault();
            }
        }
    }

    public void Write(T value)
    {
        lock (_lock)
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(directory);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, Path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_lock)
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }

    private void TryMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Bozuk dosya yedeklenemedi");
        }
    }
}

public sealed class JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore> logger) : ISettingsStore
{
    private readonly JsonFile<AppSettings> _file = new(paths.SettingsFile, () => new AppSettings(), logger);

    public AppSettings Load() => _file.Read().Normalize();

    public void Save(AppSettings settings) => _file.Write(settings.Normalize());
}

public sealed class JsonHistoryStore(IAppPaths paths, ILogger<JsonHistoryStore> logger) : IHistoryStore
{
    /// <summary>Oldest entries beyond this limit are dropped to keep the file small.</summary>
    public const int MaxEntries = 2000;

    private readonly JsonFile<List<HistoryEntry>> _file = new(paths.HistoryFile, () => [], logger);

    public IReadOnlyList<HistoryEntry> Load() =>
        _file.Read().OrderByDescending(e => e.Timestamp).ToList();

    public void Append(IEnumerable<HistoryEntry> entries)
    {
        var all = _file.Read();
        all.AddRange(entries);
        if (all.Count > MaxEntries)
            all = all.OrderByDescending(e => e.Timestamp).Take(MaxEntries).ToList();
        _file.Write(all);
    }

    public void Clear() => _file.Delete();
}

public sealed record CachedPackageInfo(string? Version, long? SizeBytes, DateTimeOffset CheckedAt);

/// <summary>Remembers verified versions and download sizes so cards can show sizes after a restart.</summary>
public sealed class PackageInfoCache(IAppPaths paths, ILogger<PackageInfoCache> logger)
{
    private readonly JsonFile<Dictionary<string, CachedPackageInfo>> _file =
        new(System.IO.Path.Combine(paths.CacheDirectory, "package-info.json"), () => new(StringComparer.Ordinal), logger);

    private Dictionary<string, CachedPackageInfo>? _entries;
    private readonly Lock _lock = new();

    public CachedPackageInfo? Get(string softwareId)
    {
        lock (_lock)
        {
            _entries ??= new Dictionary<string, CachedPackageInfo>(_file.Read(), StringComparer.Ordinal);
            return _entries.GetValueOrDefault(softwareId);
        }
    }

    public void Set(string softwareId, CachedPackageInfo info)
    {
        lock (_lock)
        {
            _entries ??= new Dictionary<string, CachedPackageInfo>(_file.Read(), StringComparer.Ordinal);
            _entries[softwareId] = info;
            try
            {
                _file.Write(_entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Paket bilgisi önbelleği yazılamadı");
            }
        }
    }
}
