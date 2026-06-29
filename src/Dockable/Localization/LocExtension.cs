using System.Windows.Data;
using System.Windows.Markup;

namespace Dockable.Localization;

/// <summary>
/// XAML markup extension: <c>{loc:Loc Key=Section_System}</c> binds the target property to
/// <see cref="Loc"/>'s indexer for that key, so the text updates live when the language changes
/// (<see cref="Loc"/> raises <c>PropertyChanged("Item[]")</c> on <see cref="Loc.SetLanguage"/>).
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension() { }

    public LocExtension(string key) => Key = key;

    /// <summary>The string key to look up in the active language table.</summary>
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
