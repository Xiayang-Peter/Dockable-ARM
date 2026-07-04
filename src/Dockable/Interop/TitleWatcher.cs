using Windows.Win32;
using Windows.Win32.Foundation;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="TitleChanged"/> when the focused window changes (EVENT_SYSTEM_FOREGROUND) or
/// when that window edits its own title bar text in place (EVENT_OBJECT_NAMECHANGE — e.g. a browser
/// switching tabs). Both WinEvent hooks are delivered on the registering UI thread. Used by the menu
/// bar to keep the focused-window title live.
/// </summary>
public sealed class TitleWatcher : IDisposable
{
    private readonly WinEventHook _foregroundHook;
    private readonly WinEventHook _nameHook;
    private HWND _foreground; // the window whose name changes we currently care about

    public event Action? TitleChanged;

    public TitleWatcher()
    {
        _foregroundHook = new WinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            (hwnd, _) =>
            {
                _foreground = hwnd;
                TitleChanged?.Invoke();
            });
        _nameHook = new WinEventHook(PInvoke.EVENT_OBJECT_NAMECHANGE, PInvoke.EVENT_OBJECT_NAMECHANGE,
            (hwnd, _) =>
            {
                // The focused window retitled itself (tab switch, document edit, …).
                if (hwnd == _foreground)
                    TitleChanged?.Invoke();
            });
    }

    public void Start()
    {
        if (_foregroundHook.IsActive)
            return;
        _foregroundHook.Start();
        _nameHook.Start();
        _foreground = PInvoke.GetForegroundWindow();
    }

    public void Dispose()
    {
        _foregroundHook.Dispose();
        _nameHook.Dispose();
    }
}
