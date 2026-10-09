using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeployNest.Infrastructure.Logging;

/// <summary>
/// Writes structured JSON-lines logs to %LOCALAPPDATA%\DeployNest\logs (one file per day,
/// kept for 14 days). The user's profile path and user name are masked before writing so logs
/// can be shared in bug reports.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int RetentionDays = 14;
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _writeLock = new();
    private readonly string _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _userName = Environment.UserName;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
    }

    public string CurrentFile => Path.Combine(_directory, $"deploynest-{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, ShortCategory(categoryName));

    public void Dispose()
    {
    }

    internal string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        if (_profile.Length > 3)
            text = text.Replace(_profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        if (_userName.Length > 2)
            text = text.Replace(_userName, "<kullanıcı>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private void Write(LogLevel level, string category, string message, Exception? exception, IEnumerable<KeyValuePair<string, object?>>? state)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("t", DateTimeOffset.Now.ToString("O"));
            json.WriteString("lvl", level.ToString());
            json.WriteString("cat", category);
            json.WriteString("msg", Redact(message));
            if (state is not null)
            {
                foreach (var (key, value) in state)
                {
                    if (key == "{OriginalFormat}" || value is null)
                        continue;
                    json.WriteString(key, Redact(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty));
                }
            }
            if (exception is not null)
                json.WriteString("ex", Redact(exception.ToString()));
            json.WriteEndObject();
        }

        var line = Encoding.UTF8.GetString(buffer.ToArray());
        lock (_writeLock)
        {
            try
            {
                File.AppendAllText(CurrentFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never crash the application.
            }
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "deploynest-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-RetentionDays))
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ShortCategory(string category)
    {
        var index = category.LastIndexOf('.');
        return index >= 0 ? category[(index + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            provider.Write(logLevel, category, formatter(state, exception), exception, state as IEnumerable<KeyValuePair<string, object?>>);
        }
    }
}
