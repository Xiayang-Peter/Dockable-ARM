using Microsoft.Win32;

namespace Dockable.Interop;

/// <summary>
/// Manages per-user "run at Windows startup" entries via the HKCU Run key
/// (<c>Software\Microsoft\Windows\CurrentVersion\Run</c>). Entries are keyed by a stable name and
/// hold the quoted command to launch. Self-managed: the checkmark reflects an entry we created.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Whether a startup entry named <paramref name="name"/> currently exists.</summary>
    public static bool IsEnabled(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(name) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds (or updates) a startup entry that launches <paramref name="command"/>.</summary>
    public static void Enable(string name, string command)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key?.SetValue(name, $"\"{command}\"");
        }
        catch
        {
            // Best-effort; never crash the app over a startup toggle.
        }
    }

    /// <summary>Removes the startup entry named <paramref name="name"/>, if present.</summary>
    public static void Disable(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(name) is not null)
                key.DeleteValue(name, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort.
        }
    }
}
