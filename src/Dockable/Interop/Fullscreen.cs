using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Interop;

/// <summary>
/// Detects when the foreground window fully covers a monitor — i.e. an exclusive-fullscreen or
/// borderless windowed-fullscreen app (games, video players). The dock and menu bar hide while such a
/// window owns their monitor so they never sit on top of full-screen content.
/// </summary>
public static class Fullscreen
{
    /// <summary>True when the foreground window belongs to our own process (so callers can avoid
    /// reacting to the user interacting with the dock / menu bar / our popups).</summary>
    public static unsafe bool IsForegroundOwnProcess(uint ownProcessId)
    {
        HWND fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull)
            return false;
        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(fg, &pid);
        return pid == ownProcessId;
    }

    /// <summary>
    /// True when the foreground window covers the whole monitor that <paramref name="referenceHwnd"/>
    /// sits on, excluding the desktop/shell and our own windows (process <paramref name="ownProcessId"/>).
    /// </summary>
    public static unsafe bool IsForegroundFullscreenOnMonitorOf(IntPtr referenceHwnd, uint ownProcessId)
    {
        if (referenceHwnd == IntPtr.Zero)
            return false;

        HWND fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull || fg == (HWND)referenceHwnd
            || fg == PInvoke.GetDesktopWindow() || fg == PInvoke.GetShellWindow())
            return false;

        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(fg, &pid);
        if (pid == ownProcessId) // our own backdrop / overlays / popups
            return false;

        // Only count it if the full-screen window is on the same monitor as the reference window.
        var fgMon = PInvoke.MonitorFromWindow(fg, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var refMon = PInvoke.MonitorFromWindow((HWND)referenceHwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (fgMon != refMon)
            return false;

        if (!PInvoke.GetWindowRect(fg, out RECT r))
            return false;
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(fgMon, ref mi))
            return false;

        var m = mi.rcMonitor;
        return r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom;
    }
}
