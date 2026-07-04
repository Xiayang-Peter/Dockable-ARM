using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Synthesizes keyboard chords via SendInput: presses the keys in order, then releases them in
/// reverse (e.g. Win down, B down, B up, Win up). Shared by the OS-gesture openers (Start, Quick
/// Settings, Notifications, tray overflow).
/// </summary>
internal static class SynthesizedInput
{
    /// <summary>Sends one chord: all keys pressed in order, then released in reverse.</summary>
    internal static void SendChord(params VIRTUAL_KEY[] keys)
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
