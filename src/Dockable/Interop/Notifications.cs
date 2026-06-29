using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 Notification Center / calendar flyout. Like <see cref="StartMenu"/> and
/// <see cref="QuickSettings"/>, there is no public API to invoke it, so we synthesize its hotkey
/// (Win+N). The OS anchors the flyout to the bottom-right; we cannot reposition it.
/// </summary>
public static class Notifications
{
    public static void Open()
    {
        Span<INPUT> inputs =
        [
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_N, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_N, keyUp: true),
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: true),
        ];
        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
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
