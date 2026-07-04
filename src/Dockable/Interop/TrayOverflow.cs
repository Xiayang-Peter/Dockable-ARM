using System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Opens the Windows 11 system-tray overflow ("show hidden icons") flyout by synthesizing the shell's
/// own keyboard gesture: <c>Win+B</c> moves focus to the notification area (revealing an auto-hidden
/// taskbar) and selects its first item — the overflow chevron — then <c>Enter</c> activates it. This is
/// language-independent and doesn't depend on the tray's (XAML, build-specific) internals.
/// Call off the UI thread — the settle sleep would otherwise stall it.
/// </summary>
public static class TrayOverflow
{
    public static void Open()
    {
        SynthesizedInput.SendChord(VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_B);
        Thread.Sleep(200); // let the shell focus the tray before we press Enter
        SynthesizedInput.SendChord(VIRTUAL_KEY.VK_RETURN);
    }
}
