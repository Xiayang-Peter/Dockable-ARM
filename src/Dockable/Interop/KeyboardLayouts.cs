using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Dockable.Interop;

/// <summary>
/// Reads (Phase A) and switches (Phase B) the keyboard layout. The "current" layout is the one of the
/// foreground window's thread — Windows tracks the active input language per thread — so the menu bar
/// reflects whatever the user is typing into.
/// </summary>
public static class KeyboardLayouts
{
    // WM_INPUTLANGCHANGEREQUEST: ask a window to switch its thread's active keyboard layout. Not in the
    // Win32 metadata, so defined here.
    private const uint WM_INPUTLANGCHANGEREQUEST = 0x0050;

    /// <summary>The active layout's two-letter language code (e.g. "EN", "PT"), or empty if unknown.</summary>
    public static string CurrentTwoLetter() => TwoLetterFor(CurrentLayoutHandle());

    /// <summary>The HKL (as a native handle) active on the foreground window's thread.</summary>
    internal static unsafe nint CurrentLayoutHandle()
    {
        HWND fg = PInvoke.GetForegroundWindow();
        uint tid = fg.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(fg, null);
        return (nint)PInvoke.GetKeyboardLayout(tid);
    }

    /// <summary>All installed keyboard layouts as (HKL handle, two-letter label) pairs, for the switcher.</summary>
    public static unsafe IReadOnlyList<(nint Hkl, string Label)> Installed()
    {
        int count = PInvoke.GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return Array.Empty<(nint, string)>();

        var handles = new HKL[count];
        fixed (HKL* p = handles)
            count = PInvoke.GetKeyboardLayoutList(count, p);

        var result = new List<(nint, string)>(count);
        for (int i = 0; i < count; i++)
        {
            nint h = (nint)handles[i];
            result.Add((h, TwoLetterFor(h)));
        }
        return result;
    }

    /// <summary>Switches the foreground window's thread to the given layout (best-effort).</summary>
    public static void Switch(nint hkl)
    {
        HWND fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull)
            return;
        // Post (don't send) so we don't block on the target's message loop; it changes that app's input
        // language, which is what the user sees in the menu bar.
        PInvoke.PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, default, (LPARAM)hkl);
    }

    private static string TwoLetterFor(nint hkl)
    {
        // The low word of an HKL is the language identifier (LANGID).
        int langId = (int)(hkl & 0xFFFF);
        if (langId == 0)
            return string.Empty;
        try
        {
            return new CultureInfo(langId).TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch (CultureNotFoundException)
        {
            return string.Empty;
        }
    }
}
