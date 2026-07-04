using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Interop;

/// <summary>
/// Owns one SetWinEventHook registration: keeps the WINEVENTPROC delegate alive for the hook's
/// lifetime, filters callbacks to the window itself (OBJID_WINDOW / CHILDID_SELF — every consumer
/// wants that), guards double-starts, supports stop/restart, and unhooks via the SafeHandle on
/// dispose. With WINEVENT_OUTOFCONTEXT (the default flags) callbacks are delivered on the
/// registering thread, so no marshalling is needed.
/// </summary>
internal sealed class WinEventHook : IDisposable
{
    private readonly uint _eventMin;
    private readonly uint _eventMax;
    private readonly uint _flags;
    private readonly Action<HWND, uint> _onEvent;
    private readonly WINEVENTPROC _proc; // held so the native hook's delegate is never collected
    private UnhookWinEventSafeHandle? _hook;

    public WinEventHook(uint eventMin, uint eventMax, Action<HWND, uint> onEvent,
        uint flags = PInvoke.WINEVENT_OUTOFCONTEXT)
    {
        _eventMin = eventMin;
        _eventMax = eventMax;
        _flags = flags;
        _onEvent = onEvent;
        _proc = OnWinEvent;
    }

    /// <summary>Whether the hook is currently registered.</summary>
    public bool IsActive => _hook is { IsInvalid: false };

    /// <summary>Registers the hook; no-op when already active. <paramref name="idProcess"/> scopes
    /// it to one process (0 = system-wide). Restartable after <see cref="Stop"/>.</summary>
    public void Start(uint idProcess = 0)
    {
        if (IsActive)
            return;
        _hook = PInvoke.SetWinEventHook(_eventMin, _eventMax, default, _proc, idProcess, 0, _flags);
    }

    /// <summary>Unregisters the hook (the SafeHandle calls UnhookWinEvent).</summary>
    public void Stop()
    {
        _hook?.Dispose();
        _hook = null;
    }

    public void Dispose() => Stop();

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        if (idObject == 0 && idChild == 0) // the window itself, not a child UI element
            _onEvent(hwnd, @event);
    }
}
