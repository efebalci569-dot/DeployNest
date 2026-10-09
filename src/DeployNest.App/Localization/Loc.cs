using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using DeployNest.Core.Localization;

namespace DeployNest.App.Localization;

/// <summary>Bindable view of the localizer; raises a change for every key when the language changes.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private ILocalizer _localizer = new JsonLocalizer();

    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _localizer[key];

    public void Attach(ILocalizer localizer)
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
        _localizer = localizer;
        _localizer.LanguageChanged += OnLanguageChanged;
        OnLanguageChanged(this, EventArgs.Empty);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>XAML usage: Text="{l:Loc Home.Title}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = LocalizationSource.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
