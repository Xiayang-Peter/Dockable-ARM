using System.Runtime.InteropServices;
using System.Threading;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 system-tray overflow ("show hidden icons") flyout by synthesizing the shell's
/// own keyboard gesture: <c>Win+B</c> moves focus to the notification area (revealing an auto-hidden
/// taskbar) and selects its first item — the overflow chevron — then <c>Enter</c> activates it. This is
/// language-independent and doesn't depend on the tray's (XAML, build-specific) internals.
/// </summary>
public static class TrayOverflow
{
    public static void Open()
    {
        Send(VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_B);
        Thread.Sleep(200); // let the shell focus the tray before we press Enter
        Send(VIRTUAL_KEY.VK_RETURN);
    }

    // Presses the keys in order, then releases them in reverse (a chord, e.g. Win down, B down, B up,
    // Win up).
    private static void Send(params VIRTUAL_KEY[] keys)
    {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++)
            inputs[i] = KeyEvent(keys[i], keyUp: false);
        for (int i = 0; i < keys.Length; i++)
            inputs[keys.Length + i] = KeyEvent(keys[keys.Length - 1 - i], keyUp: true);
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
