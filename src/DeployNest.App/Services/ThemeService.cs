using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeployNest.Core.Settings;
using Microsoft.Win32;

namespace DeployNest.App.Services;

/// <summary>
/// Applies light/dark themes: swaps the DeployNest token dictionary, switches the WPF Fluent
/// theme used by standard controls and colors the window title bar to match.
/// </summary>
public sealed partial class ThemeService
{
    private const string LightSource = "pack://application:,,,/DeployNest;component/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/DeployNest;component/Themes/Dark.xaml";

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Preference == ThemePreference.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ThemePreference.System));
        };
    }

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        var dark = preference == ThemePreference.Dark || (preference == ThemePreference.System && SystemUsesDarkTheme());
        IsDark = dark;

        var app = Application.Current;
        var dictionaries = app.Resources.MergedDictionaries;
        var tokens = new ResourceDictionary { Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute) };
        var existing = dictionaries.FirstOrDefault(d => d.Contains("Dn.WindowBackground"));
        if (existing is null)
            dictionaries.Insert(0, tokens);
        else
            dictionaries[dictionaries.IndexOf(existing)] = tokens;

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        foreach (Window window in app.Windows)
            ApplyTitleBar(window);

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var value = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 20H1+ and Windows 11). Ignored where unsupported.
        _ = DwmSetWindowAttribute(handle, 20, ref value, sizeof(int));
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
