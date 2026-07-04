using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Dockable.Localization;

namespace Dockable;

/// <summary>
/// A small, light, centered single-line text prompt with OK / Cancel — the input counterpart to
/// <see cref="ConfirmDialog"/>. <see cref="Window.ShowDialog"/> returns true (OK) / false (Cancel);
/// <see cref="Value"/> holds the entered text.
/// </summary>
internal sealed class InputDialog : Window
{
    private readonly TextBox _input;

    public string Value => _input.Text;

    public InputDialog(string message, string initialValue)
    {
        DialogChrome.ApplyShell(this);

        var stack = new StackPanel();
        stack.Children.Add(DialogChrome.Message(message, bottomMargin: 12));

        _input = new TextBox
        {
            Text = initialValue,
            FontSize = 14,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 18),
        };
        // Name the field after the prompt so screen readers announce what's being asked.
        System.Windows.Automation.AutomationProperties.SetName(_input, message);
        stack.Children.Add(_input);

        DialogChrome.AddButtonRow(stack, this, Loc.T("Common_Cancel"), Loc.T("Common_OK"));

        Content = DialogChrome.Surface(stack);

        // Focus the field and pre-select its text so the user can type a replacement immediately.
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
