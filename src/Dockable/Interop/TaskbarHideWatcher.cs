using Windows.Win32;

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
    // We only care that a tray window just became visible — re-hide it (our own SW_HIDE fires
    // EVENT_OBJECT_HIDE, which we don't listen for, so there's no feedback loop).
    private readonly WinEventHook _hook = new(PInvoke.EVENT_OBJECT_SHOW, PInvoke.EVENT_OBJECT_SHOW,
        (hwnd, _) =>
        {
            if (Taskbar.IsTrayWindow(hwnd))
                Taskbar.Hide();
        });

    /// <summary>Starts re-hiding the taskbar whenever Explorer shows it. No-op if already running or the
    /// taskbar process can't be found.</summary>
    public void Start()
    {
        if (_hook.IsActive)
            return;
        uint trayPid = Taskbar.TrayProcessId();
        if (trayPid == 0)
            return;
        _hook.Start(trayPid);
    }

    /// <summary>Stops watching (the taskbar is left in whatever state it's in).</summary>
    public void Stop() => _hook.Stop();

    public void Dispose() => Stop();
}
