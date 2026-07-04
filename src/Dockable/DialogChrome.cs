using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Dockable;

/// <summary>
/// The shared scaffolding of the small code-built dialogs (<see cref="ConfirmDialog"/>,
/// <see cref="InputDialog"/>): the frameless centered window shell, the message block, the
/// rounded drop-shadowed surface card, and the Cancel/OK-style button row (negative = IsCancel /
/// DialogResult=false, positive = IsDefault / DialogResult=true). Per-dialog content (checkbox,
/// text field, margins) stays with each dialog.
/// </summary>
internal static class DialogChrome
{
    /// <summary>Applies the shared frameless-dialog window properties.</summary>
    public static void ApplyShell(Window dialog)
    {
        dialog.Title = "Dockable";
        dialog.Icon = AppIcon.Large;
        dialog.WindowStyle = WindowStyle.None;
        dialog.AllowsTransparency = true;
        dialog.Background = Brushes.Transparent;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.SizeToContent = SizeToContent.Height;
        dialog.Width = 380;
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowInTaskbar = false;
        dialog.Topmost = true;
    }

    /// <summary>The wrapped message TextBlock, with the dialog's own bottom spacing.</summary>
    public static TextBlock Message(string text, double bottomMargin) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 14,
        Foreground = UiBrushes.Frozen(UiBrushes.InkHex),
        Margin = new Thickness(0, 0, 0, bottomMargin),
    };

    /// <summary>Adds the right-aligned button row: negative (left, Escape) then positive (right,
    /// Enter), each closing the dialog with the matching DialogResult. Returns both for callers
    /// that decorate them (e.g. automation help text).</summary>
    public static (Button Negative, Button Positive) AddButtonRow(Panel host, Window dialog,
        string negativeText, string positiveText)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var negative = new Button { Content = negativeText, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 5, 10, 5), IsCancel = true };
        negative.Click += (_, _) => dialog.DialogResult = false;
        var positive = new Button { Content = positiveText, MinWidth = 84, Padding = new Thickness(10, 5, 10, 5), IsDefault = true };
        positive.Click += (_, _) => dialog.DialogResult = true;
        row.Children.Add(negative);
        row.Children.Add(positive);
        host.Children.Add(row);
        return (negative, positive);
    }

    /// <summary>Wraps the dialog content in the rounded, drop-shadowed surface card.</summary>
    public static Border Surface(UIElement content) => new()
    {
        Background = UiBrushes.Frozen(UiBrushes.SurfaceHex),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(22),
        Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.35, Color = Colors.Black },
        Child = content,
    };
}
