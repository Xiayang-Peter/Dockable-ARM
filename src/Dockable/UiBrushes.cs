using System.Windows.Media;

namespace Dockable;

/// <summary>Shared frozen-solid-brush helper plus the recurring accent/ink hex constants, so the
/// palette can't drift between the windows that re-declare them.</summary>
internal static class UiBrushes
{
    /// <summary>macOS-style accent blue.</summary>
    public const string AccentHex = "#0A84FF";

    /// <summary>Near-black text.</summary>
    public const string InkHex = "#1D1D1F";

    /// <summary>Near-white dialog surface.</summary>
    public const string SurfaceHex = "#F5F5F7";

    /// <summary>A frozen SolidColorBrush parsed from an ARGB/RGB hex string.</summary>
    public static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
