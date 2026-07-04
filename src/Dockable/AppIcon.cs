using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Dockable;

/// <summary>
/// The application's own icon, loaded once from the embedded resources for use as window icons
/// (<see cref="Large"/>) and the tray icon (<see cref="Tray"/>).
/// </summary>
internal static class AppIcon
{
    /// <summary>256px app icon (window / title-bar / Alt-Tab).</summary>
    public static BitmapImage Large { get; } = Load("Assets/Dockable.png");

    /// <summary>The multi-size .ico (16–256px frames) for the system tray. H.NotifyIcon reads the
    /// URI-backed stream to build the native icon, so the shell picks the crisp small frame for the
    /// notification area instead of downscaling a large bitmap.</summary>
    public static BitmapImage Tray { get; } = Load("Assets/Dockable.ico");

    /// <summary>The macOS-style Settings icon used by the built-in Dock Preferences tile.</summary>
    public static ImageSource Preferences { get; } = Load("Assets/settings.png");

    private static BitmapImage Load(string relativePath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri($"pack://application:,,,/{relativePath}");
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        bitmap.Freeze(); // cross-thread / reusable
        return bitmap;
    }
}
