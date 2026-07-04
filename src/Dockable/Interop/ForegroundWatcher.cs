using Windows.Win32;

namespace Dockable.Interop;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> whenever the foreground window changes (via a
/// EVENT_SYSTEM_FOREGROUND WinEvent hook, delivered on the registering UI thread). Used to react
/// promptly when a full-screen app takes over so the dock can get out of the way.
/// </summary>
public sealed class ForegroundWatcher : IDisposable
{
    private readonly WinEventHook _hook;

    public event Action? ForegroundChanged;

    public ForegroundWatcher()
        => _hook = new WinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            (_, _) => ForegroundChanged?.Invoke());

    public void Start() => _hook.Start();

    public void Dispose() => _hook.Dispose();
}
