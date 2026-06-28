using System.Windows;
using Dockable.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Dockable.Interop;

/// <summary>
/// Registers the dock as a Win32 AppBar so it can reserve screen space along an edge
/// (the always-visible docking mode), pushing maximized windows out of the way.
/// Coordinates are physical pixels. Auto-hide is handled separately by the window
/// (a custom slide), not via ABM_SETAUTOHIDEBAR.
/// </summary>
public sealed class AppBarManager
{
    private readonly IntPtr _hwnd;
    private readonly uint _callbackMessage;
    private bool _registered;

    /// <param name="callbackMessage">Private window message the shell uses for ABN_* notifications.</param>
    public AppBarManager(IntPtr hwnd, uint callbackMessage)
    {
        _hwnd = hwnd;
        _callbackMessage = callbackMessage;
    }

    public bool IsRegistered => _registered;

    public unsafe void Register()
    {
        if (_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, &data);
        _registered = true;
    }

    public unsafe void Unregister()
    {
        if (!_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_REMOVE, &data);
        _registered = false;
    }

    /// <summary>
    /// Reserves a strip of <paramref name="thicknessPx"/> along the given <paramref name="edge"/> of
    /// <paramref name="monitorPx"/> and returns the granted rectangle (physical pixels).
    /// </summary>
    public unsafe Int32Rect ReserveEdge(DockEdge edge, Rect monitorPx, int thicknessPx)
    {
        int left = (int)Math.Round(monitorPx.Left);
        int top = (int)Math.Round(monitorPx.Top);
        int right = (int)Math.Round(monitorPx.Right);
        int bottom = (int)Math.Round(monitorPx.Bottom);

        var data = NewData();
        data.uEdge = edge switch
        {
            DockEdge.Top => PInvoke.ABE_TOP,
            DockEdge.Left => PInvoke.ABE_LEFT,
            DockEdge.Right => PInvoke.ABE_RIGHT,
            _ => PInvoke.ABE_BOTTOM,
        };
        data.rc = edge switch
        {
            DockEdge.Top => new RECT { left = left, top = top, right = right, bottom = top + thicknessPx },
            DockEdge.Left => new RECT { left = left, top = top, right = left + thicknessPx, bottom = bottom },
            DockEdge.Right => new RECT { left = right - thicknessPx, top = top, right = right, bottom = bottom },
            _ => new RECT { left = left, top = bottom - thicknessPx, right = right, bottom = bottom },
        };

        // QUERYPOS lets the shell adjust the proposed rect around existing appbars (e.g. the
        // taskbar); we then re-assert our thickness on the docked edge before SETPOS grants it.
        PInvoke.SHAppBarMessage(PInvoke.ABM_QUERYPOS, &data);
        switch (edge)
        {
            case DockEdge.Top: data.rc.bottom = data.rc.top + thicknessPx; break;
            case DockEdge.Left: data.rc.right = data.rc.left + thicknessPx; break;
            case DockEdge.Right: data.rc.left = data.rc.right - thicknessPx; break;
            default: data.rc.top = data.rc.bottom - thicknessPx; break;
        }
        PInvoke.SHAppBarMessage(PInvoke.ABM_SETPOS, &data);

        return new Int32Rect(data.rc.left, data.rc.top,
            data.rc.right - data.rc.left, data.rc.bottom - data.rc.top);
    }

    /// <summary>Tells the shell the appbar window moved, so it re-validates the layout.</summary>
    public unsafe void NotifyPosChanged()
    {
        if (!_registered)
            return;
        var data = NewData();
        PInvoke.SHAppBarMessage(PInvoke.ABM_WINDOWPOSCHANGED, &data);
    }

    private unsafe APPBARDATA NewData() => new()
    {
        cbSize = (uint)sizeof(APPBARDATA),
        hWnd = (HWND)_hwnd,
        uCallbackMessage = _callbackMessage,
    };
}
