using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 Quick Settings flyout (network / sound / brightness). There is no public API
/// to invoke or position it, so — like <see cref="StartMenu"/> — we synthesize its hotkey (Win+A) via
/// SendInput. The OS anchors the flyout to the bottom-right system tray; we cannot reposition it.
/// </summary>
public static class QuickSettings
{
    public static void Open()
    {
        Span<INPUT> inputs =
        [
            KeyEvent(VIRTUAL_KEY.VK_LWIN, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_A, keyUp: false),
            KeyEvent(VIRTUAL_KEY.VK_A, keyUp: true),
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
