using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 Quick Settings flyout (network / sound / brightness). There is no public API
/// to invoke or position it, so — like <see cref="StartMenu"/> — we synthesize its hotkey (Win+A) via
/// SendInput. The OS anchors the flyout to the bottom-right system tray; we cannot reposition it.
/// </summary>
public static class QuickSettings
{
    public static void Open() => SynthesizedInput.SendChord(VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_A);
}
