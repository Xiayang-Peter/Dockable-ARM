using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> whenever the foreground window changes (via a
/// EVENT_SYSTEM_FOREGROUND WinEvent hook, delivered on the registering UI thread). Used to react
/// promptly when a full-screen app takes over so the dock can get out of the way.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly WINEVENTPROC _proc; // held to keep the delegate alive for the hook
    private UnhookWinEventSafeHandle? _hook;

    public event Action? ForegroundChanged;

    public ForegroundWatcher() => _proc = OnWinEvent;

    public void Start()
    {
        if (_hook is { IsInvalid: false })
            return;
        _hook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            default, _proc, idProcess: 0, idThread: 0, PInvoke.WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        if (idObject == 0 && idChild == 0) // the window itself, not a child element
            ForegroundChanged?.Invoke();
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;
    }
}
