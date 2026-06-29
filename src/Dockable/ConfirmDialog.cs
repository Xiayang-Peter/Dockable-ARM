using System.Windows;
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

    public ConfirmDialog(string message)
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
            Margin = new Thickness(0, 0, 0, 16),
        });

        _doNotAsk = new CheckBox
        {
            Content = Loc.T("Common_DoNotAskAgain"),
            IsChecked = false,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
            Margin = new Thickness(0, 0, 0, 18),
        };
        stack.Children.Add(_doNotAsk);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new Button { Content = Loc.T("Common_No"), MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5), IsCancel = true };
        no.Click += (_, _) => DialogResult = false;
        var yes = new Button { Content = Loc.T("Common_Yes"), MinWidth = 84, Padding = new Thickness(10, 5, 10, 5), IsDefault = true };
        yes.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
            Child = stack,
        };
    }
}
