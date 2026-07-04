using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Dockable.Localization;

namespace Dockable;

/// <summary>
/// A small, light, centered Yes/No dialog with an optional "Do not ask again" checkbox. Built in code
/// (no XAML) since it's only used for a couple of simple prompts. <see cref="Window.ShowDialog"/>
/// returns true (Yes) / false (No / dismissed); <see cref="DoNotAskAgain"/> reflects the checkbox.
/// </summary>
internal sealed class ConfirmDialog : Window
{
    private readonly CheckBox _doNotAsk;

    public bool DoNotAskAgain => _doNotAsk.IsChecked == true;

    public ConfirmDialog(string message, bool showDoNotAskAgain = true)
    {
        DialogChrome.ApplyShell(this);

        var stack = new StackPanel();
        stack.Children.Add(DialogChrome.Message(message, bottomMargin: 16));

        _doNotAsk = new CheckBox
        {
            Content = Loc.T("Common_DoNotAskAgain"),
            IsChecked = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            Margin = new Thickness(0, 0, 0, 18),
        };
        if (showDoNotAskAgain)
            stack.Children.Add(_doNotAsk);

        var (no, yes) = DialogChrome.AddButtonRow(stack, this, Loc.T("Common_No"), Loc.T("Common_Yes"));
        // Screen readers land on a button first (borderless window, no title bar to read) — carry
        // the question as help text so it's announced with the focus.
        AutomationProperties.SetHelpText(yes, message);
        AutomationProperties.SetHelpText(no, message);

        Content = DialogChrome.Surface(stack);
    }
}
