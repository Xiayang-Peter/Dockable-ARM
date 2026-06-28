using System.Runtime.InteropServices;
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
    public static void Open()
    {
        Span<INPUT> inputs =
        [
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: true),
        ];

        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

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

    private static INPUT KeyEvent(VIRTUAL_KEY key, bool keyUp)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = key,
            dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
        };
        return input;
    }
}
