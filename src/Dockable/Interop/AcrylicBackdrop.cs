using System.Numerics;
using System.Runtime.InteropServices;
using Dockable.Models;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable.Interop;

/// <summary>
/// A separate, non-layered, click-through backdrop window that renders a live acrylic blur of the
/// desktop behind the dock, clipped to the dock bar's rounded rectangle and z-ordered just below the
/// dock's icon window. Hosts a <see cref="Windows.UI.Composition"/> tree on its own
/// <c>WS_EX_NOREDIRECTIONBITMAP</c> HWND.
///
/// Stage 1: a host-backdrop brush (standard acrylic blur) + rounded clip + bar tracking. The custom
/// 20px-blur / 180%-saturation / liquid-glass displacement effect graph is layered on in later stages.
/// </summary>
public sealed class AcrylicBackdrop : IDisposable
{
    private const string WindowClass = "DockableAcrylicBackdrop";

    // --- DispatcherQueue (required before creating a Compositor on this thread) ---
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;      // DQTYPE_THREAD_CURRENT = 2
        public int apartmentType;   // DQTAT_COM_STA = 2
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object? dispatcherQueueController);

    // QI'd off the Compositor to bind it to a desktop HWND.
    [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(IntPtr hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr result);
    }

    private object? _dispatcherController; // kept alive for the lifetime of the compositor
    private WNDPROC? _wndProc;             // kept alive so the window class proc isn't collected
    private HWND _hwnd;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private SpriteVisual? _visual;
    private CompositionRoundedRectangleGeometry? _clipGeometry;
    private bool _ready;

    /// <summary>Creates the backdrop window and Composition tree. Safe to call once; no-op afterwards.</summary>
    public unsafe void Initialize()
    {
        if (_ready)
            return;

        EnsureDispatcherQueue();
        CreateHostWindow();

        _compositor = new Compositor();
        var interop = _compositor.As<ICompositorDesktopInterop>();
        interop.CreateDesktopWindowTarget((IntPtr)_hwnd, false, out IntPtr targetPtr);
        _target = DesktopWindowTarget.FromAbi(targetPtr);
        Marshal.Release(targetPtr);

        _visual = _compositor.CreateSpriteVisual();
        // Brush is assigned by SetEffect() (Acrylic vs Liquid Glass).

        _clipGeometry = _compositor.CreateRoundedRectangleGeometry();
        _visual.Clip = _compositor.CreateGeometricClip(_clipGeometry);

        _target.Root = _visual;
        _ready = true;
    }

    private void EnsureDispatcherQueue()
    {
        if (_dispatcherController is not null)
            return;
        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 2, // DQTAT_COM_STA (WPF UI thread is STA)
        };
        CreateDispatcherQueueController(options, out _dispatcherController);
    }

    private unsafe void CreateHostWindow()
    {
        var hmodule = PInvoke.GetModuleHandle((string?)null); // FreeLibrarySafeHandle
        var hinstance = (HINSTANCE)hmodule.DangerousGetHandle();
        _wndProc = WndProc;

        fixed (char* className = WindowClass)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _wndProc,
                hInstance = hinstance,
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(in wc); // harmless if the class already exists
        }

        // Click-through (TRANSPARENT/NOACTIVATE), out of Alt+Tab (TOOLWINDOW), no redirection surface
        // (NOREDIRECTIONBITMAP) so the host-backdrop brush samples the desktop, and topmost so it can
        // sit just under the (topmost) dock window.
        var exStyle = WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP
            | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
            | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
            | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            | WINDOW_EX_STYLE.WS_EX_TOPMOST;

        _hwnd = PInvoke.CreateWindowEx(exStyle, WindowClass, string.Empty, WINDOW_STYLE.WS_POPUP,
            0, 0, 0, 0, default, null, hmodule, null);

        int useHostBackdrop = 1;
        PInvoke.DwmSetWindowAttribute(_hwnd, DWMWINDOWATTRIBUTE.DWMWA_USE_HOSTBACKDROPBRUSH,
            &useHostBackdrop, sizeof(int));
    }

    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
        => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    /// <summary>
    /// Positions/sizes the backdrop to the dock bar (physical pixels), updates the rounded clip, and
    /// keeps it z-ordered directly beneath <paramref name="belowHwnd"/> (the dock window).
    /// </summary>
    public void SetBounds(int xPx, int yPx, int widthPx, int heightPx, float cornerRadiusPx, IntPtr belowHwnd)
    {
        if (!_ready || widthPx <= 0 || heightPx <= 0)
            return;

        var size = new Vector2(widthPx, heightPx);
        _visual!.Size = size;
        _clipGeometry!.Size = size;
        _clipGeometry.CornerRadius = new Vector2(cornerRadiusPx, cornerRadiusPx);

        // Place just below the dock window in the (topmost) z-order, without activating.
        PInvoke.SetWindowPos((HWND)_hwnd, (HWND)belowHwnd, xPx, yPx, widthPx, heightPx,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Sets the backdrop brush. Acrylic and Liquid Glass both currently use the system host-backdrop
    /// blur: a custom blur/saturation/displacement effect graph isn't achievable through CsWinRT in
    /// this app (the hand-built native effect path crashed the process), so Liquid Glass renders the
    /// same acrylic for now — safely.
    /// </summary>
    public void SetEffect(GlassEffect mode)
    {
        if (!_ready || _compositor is null || _visual is null)
            return;
        try
        {
            _visual.Brush = _compositor.CreateHostBackdropBrush();
        }
        catch
        {
            // Leave the existing brush; the dock still works.
        }
    }

    public void Show()
    {
        if (_ready)
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
    }

    public void Hide()
    {
        if (_ready)
            PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
    }

    public void Dispose()
    {
        _visual?.Dispose();
        _target?.Dispose();
        _compositor?.Dispose();
        if (!_hwnd.IsNull)
            PInvoke.DestroyWindow(_hwnd);
        _hwnd = default;
        _ready = false;
    }
}
