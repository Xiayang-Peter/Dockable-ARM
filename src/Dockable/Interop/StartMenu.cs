using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows Start menu. There is no public "open Start" API, so the
/// reliable, well-established approach is to synthesize a left-Windows keystroke
/// via SendInput.
/// </summary>
public static class StartMenu
{
    public static void Open() => SynthesizedInput.SendChord(VIRTUAL_KEY.VK_LWIN);

    /// <summary>
    /// True when a Windows shell CoreWindow is the foreground window — i.e. the Start menu (or
    /// Search/Widgets, which share the class) is up. Used to tell whether the Start menu we opened
    /// from the dock is still showing so the taskbar can stay hidden until it closes.
    /// </summary>
    public static bool IsOpen()
    {
        HWND foreground = PInvoke.GetForegroundWindow();
        return !foreground.IsNull && GetClassName(foreground) == "Windows.UI.Core.CoreWindow";
    }

    private static unsafe string GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetClassName(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }
}
