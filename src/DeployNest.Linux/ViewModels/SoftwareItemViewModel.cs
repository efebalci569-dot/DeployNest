using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DeployNest.Core.Catalog;
using DeployNest.Core.Detection;
using DeployNest.Linux.Services;

namespace DeployNest.Linux.ViewModels;

public enum PillKind
{
    Neutral,
    Success,
    Warning,
    Danger,
    Accent,
}

/// <summary>A catalog program as shown on cards and lists.</summary>
public sealed partial class SoftwareItemViewModel : ObservableObject
{
    private readonly Action<SoftwareItemViewModel> _selectionChanged;

    public SoftwareItemViewModel(SoftwareEntry entry, string categoryName, Action<SoftwareItemViewModel> selectionChanged)
    {
        Entry = entry;
        CategoryName = categoryName;
        _selectionChanged = selectionChanged;
        Logo = LogoLoader.Load(entry.Id);
        Details = BuildDetails(entry);
    }

    public SoftwareEntry Entry { get; }
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher;
    public string Summary => Entry.Summary.Get("tr");
    public string CategoryName { get; }
    public Bitmap? Logo { get; }
    public string Monogram => Entry.Icon.Monogram;
    public string Details { get; }

    /// <summary>"Flatpak", "apt"… — the ways this program can be installed on this computer.</summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    /// <summary>False when neither Flatpak nor a package for this distribution is available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    private bool _isAvailable = true;

    public bool IsUnavailable => !IsAvailable;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StatePill), nameof(IsInstalled))]
    private InstallState _state = InstallState.Checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string? _installedVersion;

    [ObservableProperty]
    private string? _evidence;

    public bool IsInstalled => State == InstallState.Installed;

    public string StateText => State switch
    {
        InstallState.Installed when !string.IsNullOrWhiteSpace(InstalledVersion) => Strings.Format("State.InstalledVersion", InstalledVersion),
        InstallState.Installed => Strings.Get("State.Installed"),
        InstallState.NotInstalled => Strings.Get("State.NotInstalled"),
        InstallState.Unverifiable => Strings.Get("State.Unverifiable"),
        _ => Strings.Get("State.Checking"),
    };

    public PillKind StatePill => State switch
    {
        InstallState.Installed => PillKind.Success,
        InstallState.Unverifiable => PillKind.Warning,
        _ => PillKind.Neutral,
    };

    partial void OnIsSelectedChanged(bool value) => _selectionChanged(this);

    public void ApplyAvailability(LinuxPackageManager? manager)
    {
        var sources = new List<string>();
        if (Entry.Linux?.FlatpakId is not null)
            sources.Add(Strings.Get("Linux.Badge.Flatpak"));
        if (manager is { } m && Entry.Linux?.PackagesFor(m).Count > 0)
            sources.Add(m.ToString().ToLowerInvariant());
        IsAvailable = sources.Count > 0;
        SourceText = sources.Count > 0 ? string.Join(" · ", sources) : Strings.Get("Linux.Badge.Unavailable");
    }

    private static string BuildDetails(SoftwareEntry entry)
    {
        var text = new StringBuilder(entry.Description.Get("tr"));
        text.AppendLine().AppendLine();
        text.Append(Strings.Format("Linux.Card.License", entry.License.Get("tr")));
        if (entry.Linux?.Notes is { } linuxNotes)
            text.AppendLine().Append(Strings.Format("Linux.Card.Note", linuxNotes.Get("tr")));
        return text.ToString();
    }
}

/// <summary>A category tile / filter chip.</summary>
public sealed record CategoryViewModel(string? Id, string Name, string IconKey, int Count)
{
    public string CountText => $"{Count} {Strings.Get("Common.ProgramsLower")}";

    public static string IconFor(string? categoryId) => categoryId switch
    {
        null => "Icon.Programs",
        "browsers" => "Icon.Globe",
        "communication" => "Icon.Chat",
        "media" => "Icon.Video",
        "creative" => "Icon.PaintBrush",
        "office" => "Icon.Document",
        "cloud" => "Icon.Cloud",
        "security" => "Icon.Shield",
        "archive" => "Icon.Archive",
        "system" => "Icon.Wrench",
        "productivity" => "Icon.Sparkle",
        "runtimes" => "Icon.Puzzle",
        "gaming" => "Icon.Games",
        "developer" => "Icon.Code",
        "downloads" => "Icon.Install",
        "ai" => "Icon.Bot",
        _ => "Icon.Programs",
    };
}

/// <summary>A preset (ready-made selection) card.</summary>
public sealed record PresetViewModel(CatalogPreset Preset, IReadOnlyList<string> ProgramNames)
{
    public string Name => Preset.Name.Get("tr");
    public string Description => Preset.Description.Get("tr");
    public string CountText => $"{Preset.SoftwareIds.Count} {Strings.Get("Common.ProgramsLower")}";
    public string Preview => string.Join(" · ", ProgramNames);

    public string IconKey => Preset.Id switch
    {
        "popular" => "Icon.Star",
        "new-pc" => "Icon.Desktop",
        "runtimes" => "Icon.Puzzle",
        "developer" => "Icon.Code",
        "gaming" => "Icon.Games",
        "office" => "Icon.Document",
        "creator" => "Icon.PaintBrush",
        "security" => "Icon.ShieldCheckmark",
        "maintenance" => "Icon.Wrench",
        _ => "Icon.Toolbox",
    };
}
