using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

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
        Title = "Dockable";
        Icon = AppIcon.Large;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x1D, 0x1F)),
            Margin = new Thickness(0, 0, 0, 12),
        });

        _input = new TextBox
        {
            Text = initialValue,
            FontSize = 14,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 18),
        };
        stack.Children.Add(_input);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5), IsCancel = true };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button { Content = "OK", MinWidth = 84, Padding = new Thickness(10, 5, 10, 5), IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
            Child = stack,
        };

        // Focus the field and pre-select its text so the user can type a replacement immediately.
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }
}
