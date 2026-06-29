using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Dockable.Localization;

namespace Dockable;

/// <summary>
/// "About Dockable" window: app icon, name, tagline, version, a short description of the stack and
/// inspiration, and the author credit. Light, macOS-style chrome matching <see cref="SettingsWindow"/>.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Icon = AppIcon.Large;
        AppImage.Source = AppIcon.Large;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        string display = version is null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionText.Text = string.Format(Loc.T("About_Version"), display);
    }

    // Open the author's GitHub page in the default browser.
    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort; a missing/blocked browser shouldn't crash the About window.
        }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
