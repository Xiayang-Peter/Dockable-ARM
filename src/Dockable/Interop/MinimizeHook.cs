using Windows.Win32;
using Windows.Win32.Foundation;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="WindowMinimizing"/> when any other process's top-level app window starts to
/// minimize and <see cref="WindowUnminimized"/> when one is restored (e.g. via the taskbar or
/// Alt+Tab), via a WinEvent hook over EVENT_SYSTEM_MINIMIZESTART..EVENT_SYSTEM_MINIMIZEEND. With
/// WINEVENT_OUTOFCONTEXT the callback is delivered on the registering (UI) thread, so no marshalling
/// is needed.
/// </summary>
public sealed class MinimizeHook : IDisposable
{
    private readonly WinEventHook _hook;
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    /// <summary>Fires with the HWND of a window beginning to minimize.</summary>
    public event Action<IntPtr>? WindowMinimizing;

    /// <summary>Fires with the HWND of a window that was just un-minimized (restored) by anything other
    /// than the dock — so a stale minimized tile can be cleared.</summary>
    public event Action<IntPtr>? WindowUnminimized;

    public MinimizeHook()
        => _hook = new WinEventHook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND,
            OnMinimizeEvent, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

    public void Start() => _hook.Start();

    private void OnMinimizeEvent(HWND hwnd, uint @event)
    {
        if (@event == PInvoke.EVENT_SYSTEM_MINIMIZESTART)
        {
            if (WindowFilter.IsEligibleAppWindow(hwnd, _ownProcessId))
                WindowMinimizing?.Invoke(hwnd);
        }
        else // EVENT_SYSTEM_MINIMIZEEND — raise for any window; the dock only acts if it tracks it.
        {
            WindowUnminimized?.Invoke((IntPtr)hwnd);
        }
    }

    public void Dispose() => _hook.Dispose();
}
