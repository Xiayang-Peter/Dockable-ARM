using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="TitleChanged"/> when the focused window changes (EVENT_SYSTEM_FOREGROUND) or
/// when that window edits its own title bar text in place (EVENT_OBJECT_NAMECHANGE — e.g. a browser
/// switching tabs). Both WinEvent hooks are delivered on the registering UI thread. Used by the menu
/// bar to keep the focused-window title live.
/// </summary>
public sealed class TitleWatcher : IDisposable
{
    private readonly WINEVENTPROC _proc; // held to keep the delegate alive for the hooks
    private UnhookWinEventSafeHandle? _foregroundHook;
    private UnhookWinEventSafeHandle? _nameHook;
    private HWND _foreground; // the window whose name changes we currently care about

    public event Action? TitleChanged;

    public TitleWatcher() => _proc = OnWinEvent;

    public void Start()
    {
        if (_foregroundHook is { IsInvalid: false })
            return;
        _foregroundHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            default, _proc, idProcess: 0, idThread: 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        _nameHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_OBJECT_NAMECHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE,
            default, _proc, idProcess: 0, idThread: 0, PInvoke.WINEVENT_OUTOFCONTEXT);
        _foreground = PInvoke.GetForegroundWindow();
    }

    private void OnWinEvent(HWINEVENTHOOK hook, uint @event, HWND hwnd, int idObject, int idChild,
        uint idEventThread, uint dwmsEventTime)
    {
        if (idObject != 0 || idChild != 0) // the window itself, not a child element
            return;

        if (@event == PInvoke.EVENT_SYSTEM_FOREGROUND)
        {
            _foreground = hwnd;
            TitleChanged?.Invoke();
        }
        else if (@event == PInvoke.EVENT_OBJECT_NAMECHANGE && hwnd == _foreground)
        {
            // The focused window retitled itself (tab switch, document edit, …).
            TitleChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        _foregroundHook?.Dispose();
        _nameHook?.Dispose();
        _foregroundHook = null;
        _nameHook = null;
    }
}
