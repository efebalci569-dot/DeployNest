namespace DeployNest.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>User preferences. Contains no credentials or other sensitive data.</summary>
public sealed record AppSettings
{
    public const int MinParallelDownloads = 1;
    public const int MaxParallelDownloadsLimit = 4;

    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public string Language { get; init; } = "tr";

    /// <summary>Folder for directly downloaded installers; null uses the default folder.</summary>
    public string? DownloadDirectory { get; init; }

    public int MaxParallelDownloads { get; init; } = 2;
    public bool KeepDownloadedFiles { get; init; }

    /// <summary>When true, programs detected as installed are skipped unless the user opts in.</summary>
    public bool SkipInstalledByDefault { get; init; } = true;

    /// <summary>Linux: install from the distribution repositories instead of Flathub when both are possible.</summary>
    public bool PreferNativePackages { get; init; }

    public AppSettings Normalize() => this with
    {
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, MinParallelDownloads, MaxParallelDownloadsLimit),
        Language = string.IsNullOrWhiteSpace(Language) ? "tr" : Language,
        DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? null : DownloadDirectory,
    };
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
