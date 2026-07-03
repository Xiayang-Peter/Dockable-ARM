using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Interop;

/// <summary>
/// Keeps the Windows taskbar hidden while the "Never" mode is active. A one-shot <c>SW_HIDE</c> doesn't
/// stick: Explorer re-shows the tray windows on its own whenever a window flashes for attention, a
/// window minimizes, or Win+M/Win+D runs — none of which Dockable triggers. This hooks Explorer's
/// window-show events (EVENT_OBJECT_SHOW, scoped to the tray's process so we don't wake for every
/// window system-wide) and re-hides the taskbar the instant it's brought back. The callback arrives on
/// the registering (UI) thread via WINEVENT_OUTOFCONTEXT, so no marshalling is needed.
/// </summary>
public sealed class TaskbarHideWatcher : IDisposable
{
    private readonly WINEVENTPROC _proc; // held to keep the delegate alive for the hook
    private UnhookWinEventSafeHandle? _hook;

    public TaskbarHideWatcher() => _proc = OnWinEvent;

    /// <summary>Starts re-hiding the taskbar whenever Explorer shows it. No-op if already running or the
    /// taskbar process can't be found.</summary>
    public void Start()
    {
        if (_hook is { IsInvalid: false })
            return;
        uint trayPid = Taskbar.TrayProcessId();
        if (trayPid == 0)
            return;
        _hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_SHOW, PInvoke.EVENT_OBJECT_SHOW,
            default, _proc, trayPid, idThread: 0, PInvoke.WINEVENT_OUTOFCONTEXT);
    }

    /// <summary>Stops watching (the taskbar is left in whatever state it's in).</summary>
    public void Stop()
    {
        _hook?.Dispose(); // SafeHandle calls UnhookWinEvent
        _hook = null;
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        // OBJID_WINDOW (0) / CHILDID_SELF (0): the window itself, not a child element. We only care that
        // a tray window just became visible — re-hide it (our own SW_HIDE fires EVENT_OBJECT_HIDE, which
        // we don't listen for, so there's no feedback loop).
        if (idObject == 0 && idChild == 0 && Taskbar.IsTrayWindow(hwnd))
            Taskbar.Hide();
    }

    public void Dispose() => Stop();
}
