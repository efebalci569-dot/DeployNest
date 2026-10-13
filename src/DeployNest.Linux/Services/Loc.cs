using Avalonia.Markup.Xaml;
using DeployNest.Core.Localization;

namespace DeployNest.Linux.Services;

/// <summary>Shared UI strings (the same JSON language files as the Windows app).</summary>
public static class Strings
{
    public static ILocalizer Localizer { get; set; } = new JsonLocalizer();

    public static string Get(string key) => Localizer[key];

    public static string Format(string key, params object?[] args) => Localizer.Format(key, args);
}

/// <summary>XAML: <c>Text="{l:Loc Home.Hero.Title}"</c>.</summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
