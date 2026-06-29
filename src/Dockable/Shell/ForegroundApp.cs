using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Dockable.Interop;

namespace Dockable.Shell;

/// <summary>
/// Resolves a friendly application display name for a window — e.g. "Google Chrome" (not "chrome", and
/// not the full window title). UWP/Store apps resolve via the window's AppUserModelID against the Apps
/// folder (the host exe, ApplicationFrameHost, isn't the app — same approach as the dock). Win32 apps
/// use the executable's <see cref="FileVersionInfo.FileDescription"/>; unreadable (elevated) windows
/// fall back to the shell display name, the title-cased file stem, then the window title.
/// </summary>
public static class ForegroundApp
{
    // Generic host processes whose FileDescription names the host, not the app the user sees — for these
    // the window title is the better label (e.g. a UWP app whose AUMID didn't resolve).
    private static readonly HashSet<string> HostExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost.exe",
    };

    // AUMID → resolved AppsFolder name (COM lookup), cached like the dock's _aumidNameCache.
    private static readonly ConcurrentDictionary<string, string?> AumidNameCache = new();

    public static string DisplayName(IntPtr hwnd, uint pid)
    {
        // UWP/Store apps: resolve the window's AppUserModelID to its AppsFolder display name.
        string aumid = TaskbarApps.GetWindowAumid(hwnd);
        if (TaskbarApps.IsPackagedAumid(aumid))
        {
            string? store = AumidNameCache.GetOrAdd(aumid,
                static id => ShortcutService.GetShellDisplayName($"shell:AppsFolder\\{id}"));
            if (!string.IsNullOrWhiteSpace(store))
                return store!;
        }

        string exe = ExePath(pid);
        if (!string.IsNullOrEmpty(exe))
        {
            string fileName = Path.GetFileName(exe);
            if (!HostExes.Contains(fileName))
            {
                try
                {
                    string? desc = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
                    if (!string.IsNullOrEmpty(desc))
                        return desc!;
                }
                catch
                {
                    // No version info / unreadable — fall through to the shell name.
                }
            }

            string? shell = ShortcutService.GetShellDisplayName(exe);
            if (!string.IsNullOrWhiteSpace(shell))
                return shell!;

            try
            {
                string stem = Path.GetFileNameWithoutExtension(exe);
                if (!string.IsNullOrEmpty(stem))
                    return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem);
            }
            catch
            {
                // ignore — fall through to the window title
            }
        }

        // Last resort (e.g. elevated apps we can't read): the window title.
        return TaskbarApps.GetWindowTitle(hwnd);
    }

    private static string ExePath(uint pid)
    {
        if (pid == 0)
            return string.Empty;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access denied (elevated / cross-bitness) or the process exited.
            return string.Empty;
        }
    }
}
