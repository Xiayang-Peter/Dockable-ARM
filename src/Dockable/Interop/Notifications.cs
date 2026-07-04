using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 Notification Center / calendar flyout. Like <see cref="StartMenu"/> and
/// <see cref="QuickSettings"/>, there is no public API to invoke it, so we synthesize its hotkey
/// (Win+N). The OS anchors the flyout to the bottom-right; we cannot reposition it.
/// </summary>
public static class Notifications
{
    public static void Open() => SynthesizedInput.SendChord(VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_N);
}
