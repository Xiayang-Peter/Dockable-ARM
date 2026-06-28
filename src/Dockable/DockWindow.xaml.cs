using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dockable.Genie;
using Dockable.Interop;
using Dockable.Models;
using Dockable.Shell;
using Dockable.ViewModels;
using H.NotifyIcon;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

public partial class DockWindow : Window
{
    // Private window message the shell uses to send the dock AppBar notifications.
    private const uint AppBarCallbackMessage = 0x0400 + 1; // WM_USER + 1
    private const int WM_SETTINGCHANGE = 0x001A;           // OS setting changed (incl. light/dark)

    // Drag ghost geometry (matches the DragGhost popup in XAML): the icon's center sits at
    // (GhostCenterX, GhostCenterY) from the popup's top-left, so it tracks under the cursor.
    private const double GhostCenterX = 80;   // half of GhostRoot's 160 width
    private const double GhostCenterY = 72;   // 42px "Remove" row + half the 60px icon
    private const int DragSteadyMs = 500;     // hold still this long to arm "Remove"
    private const double SteadyEpsilon = 4;   // px of motion that counts as "moved"

    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow; // "Dock Preferences…" window (single instance)

    private double _mouseX;
    private double _mouseY;
    private bool _hovering;
    private bool _renderingHooked;
    private bool _finalizeScheduled; // a deferred FinalizeDeparted is queued (avoid mutating Items mid-render)

    // Shared hover label: the item currently under the cursor, plus the measured label content.
    private const double LabelGap = 4; // gap above a fully-magnified icon
    private DockItemViewModel? _hoveredItem;
    private DockItemViewModel? _labelItem; // item whose text the label currently reflects

    private IntPtr _hwnd;
    private AppBarManager? _appBar;
    private bool _windowRegionClipped;      // true while the window is clipped to the resting bar

    /// <summary>True when the dock is on a side edge (Left/Right); the main axis is then screen-Y.</summary>
    private bool IsVerticalDock => ViewModel?.IsVerticalDock ?? false;

    // Keep the bar's drop shadow when the window is clipped to the resting bar.
    private const double DockRegionTopPaddingDip = 26;

    // Real window minimize/restore is intercepted and replaced with one of these effects.
    private readonly GenieAnimator _genie = new();
    private readonly ScaleAnimator _scale = new();
    private readonly MinimizeHook _minimizeHook = new();
    // Full-window captures taken while windows are visible (capture-at-minimize is too late).
    private readonly WindowThumbnailCache _thumbnails = new();
    // Live acrylic blur rendered in a separate window directly behind the bar.
    private readonly AcrylicBackdrop _acrylic = new();
    private const double BarCornerRadius = 24; // matches DockBackground's CornerRadius

    // Hide the dock entirely while a full-screen app / borderless-fullscreen game owns the screen.
    private readonly ForegroundWatcher _foreground = new();
    private bool _fullscreenActive;
    // Windows whose minimize/restore we're currently animating (ignore re-entrant events).
    private readonly HashSet<IntPtr> _busy = new();
    // Windows minimized "into" their app icon (no thumbnail tile): hwnd → its capture for restore.
    private readonly Dictionary<IntPtr, BitmapSource> _iconMinimized = new();

    // Mirror the taskbar: poll running apps + watch the pinned folder.
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;
    private readonly DispatcherTimer _appRefreshTimer;
    private FileSystemWatcher? _pinWatcher;
    private readonly DispatcherTimer _pinCheckTimer; // debounces taskbar-pin checks after folder changes
    private bool _promptOpen;                          // guards against overlapping prompt dialogs

    // While the Start menu (opened from the dock) is up, the taskbar is fully hidden; this watches
    // for it closing so we can restore the configured taskbar state.
    private readonly DispatcherTimer _startWatchTimer;
    private bool _startSeen;       // confirmed the Start menu actually appeared
    private int _startWatchTicks;  // grace ticks so we never leave the taskbar stuck hidden
    private const int StartWatchMaxTicks = 12; // ~1.8s for Start to appear before giving up

    // Drag to reorder / pin / remove.
    private Point _dragStart;
    private DockItemViewModel? _dragCandidate;
    private bool _dragInitiated;
    private Point _lastCursor;                          // cursor relative to RootCanvas
    private Point _steadyAnchor;                        // position last considered "moved"
    private bool _removeArmed;                          // dragged pinned shortcut held steady → "Remove"
    private readonly DispatcherTimer _dragSteadyTimer;  // fires after DragSteadyMs of no motion

    // Separator drag = resize the dock's Size setting (kept in [SizeMin, SizeMax], matching Dock Preferences).
    private const double SizeMin = 12;
    private const double SizeMax = 64;
    private bool _resizePressed;        // pressed a separator, not yet past the drag threshold
    private bool _separatorResize;      // actively resizing
    private double _resizeStartCursorCross; // physical-px cursor coord on the cross axis at press
    private double _resizeStartIconSize;

    public DockWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => PositionDock();
        DataContextChanged += OnDataContextChanged;

        MouseMove += OnMouseMove;
        MouseEnter += OnMouseEnter;
        MouseLeave += OnMouseLeave;
        MouseLeftButtonUp += OnDockMouseUp; // ends a custom drag (no-op for normal clicks)

        _appRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _appRefreshTimer.Tick += (_, _) =>
        {
            RefreshTaskbarApps();
            UpdateFullscreenState(); // backstop in case a fullscreen transition didn't raise an event
            CheckAndPromptNewPins(); // reliable poll for new taskbar pins (the folder watcher often doesn't fire on Win11)
        };

        _dragSteadyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DragSteadyMs) };
        _dragSteadyTimer.Tick += OnDragSteadyElapsed;
        LostMouseCapture += OnLostMouseCapture; // robust cleanup if a drag is interrupted

        _startWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _startWatchTimer.Tick += OnStartWatchTick;

        _pinCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _pinCheckTimer.Tick += (_, _) => { _pinCheckTimer.Stop(); CheckAndPromptNewPins(); };
    }

    private DockViewModel? ViewModel => DataContext as DockViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DockViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
            oldVm.AnimationRequested -= OnAnimationRequested;
        }
        if (ViewModel is { } vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.AnimationRequested += OnAnimationRequested;
            ApplyWindowSize();
            ApplyTheme(); // paint the bar for the saved theme before the window is shown
        }
    }

    // An app launch bounce started: unclip so the hop can render above the bar, and run the loop.
    private void OnAnimationRequested()
    {
        ClearWindowRegion();
        HookRendering();
    }

    // --- Light / dark theme ---

    /// <summary>Repaints the dock bar (and its theme-dependent elements) for the effective theme.</summary>
    private void ApplyTheme()
    {
        bool dark = ViewModel?.Settings.Theme switch
        {
            DockTheme.Dark => true,
            DockTheme.Light => false,
            _ => !SystemTheme.IsLight(), // System: follow Windows
        };

        if (dark)
        {
            // .macos-dock-dark
            Resources["BarBackgroundBrush"] = Brush("#66242424");
            Resources["BarBorderBrush"] = Brush("#14FFFFFF");
            Resources["SeparatorBrush"] = Brush("#40FFFFFF");
            Resources["RunningDotBrush"] = Brush("#CCFFFFFF"); // rgba(255,255,255,0.8)
            Resources["FallbackBgBrush"] = Brush("#33FFFFFF");
            Resources["FallbackTextBrush"] = Brush("#FFFFFFFF");
            Resources["IconShadowOuterOpacity"] = 0.12;
            Resources["IconShadowInnerOpacity"] = 0.18;
            BarShadow.Opacity = 0.4;
            DockBackground.BorderThickness = new Thickness(1.5);
        }
        else
        {
            // .macos-dock-light (background/border swapped from the original tints)
            Resources["BarBackgroundBrush"] = Brush("#33FFFFFF");
            Resources["BarBorderBrush"] = Brush("#66FFFFFF");
            Resources["SeparatorBrush"] = Brush("#33000000");
            Resources["RunningDotBrush"] = Brush("#B3000000"); // rgba(0,0,0,0.7)
            Resources["FallbackBgBrush"] = Brush("#1F000000");
            Resources["FallbackTextBrush"] = Brush("#CC000000");
            Resources["IconShadowOuterOpacity"] = 0.10; // subtle icon shadows on light glass
            Resources["IconShadowInnerOpacity"] = 0.14;
            BarShadow.Opacity = 0.15;
            DockBackground.BorderThickness = new Thickness(1.5); // slightly thicker border on light glass
        }
    }

    private void SetTheme(DockTheme theme)
    {
        if (ViewModel is null || ViewModel.Settings.Theme == theme)
            return;
        ViewModel.Settings.Theme = theme;
        ViewModel.Save();
        ApplyTheme();
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Binding Window.Width/Height directly is unreliable, so mirror the view-model here.
        if (e.PropertyName is nameof(DockViewModel.WindowWidth) or nameof(DockViewModel.WindowHeight))
            ApplyWindowSize();
    }

    private void ApplyWindowSize()
    {
        if (ViewModel is null)
            return;
        Width = ViewModel.WindowWidth;
        Height = ViewModel.WindowHeight;
        PositionDock();
        ApplyIdleRegion(); // re-clip to the new resting bar when the layout size changes (if idle)
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _appBar = new AppBarManager(_hwnd, AppBarCallbackMessage);

        // Mark the dock a tool window so it never shows in the Alt+Tab switcher.
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLongPtr((HWND)_hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)exStyle);

        // Listen for shell AppBar notifications (taskbar moved, full-screen app, etc.).
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        _genie.Prewarm(); // build the reusable overlays now so the first minimize is instant
        _scale.Prewarm();
        _thumbnails.Start();
        _minimizeHook.WindowMinimizing += OnWindowMinimizing;
        _minimizeHook.Start();

        _foreground.ForegroundChanged += OnForegroundChanged;
        _foreground.Start();

        // Live acrylic blur behind the bar, in its own window just below the dock.
        try
        {
            _acrylic.Initialize();
            ApplyGlassEffect();
        }
        catch
        {
            // Acrylic is a nicety; never let its setup block the dock from showing.
        }

        ApplyBehavior();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTrayIcon();
        ApplyWindowSize();
        ApplyTaskbarVisibility();
        StartTaskbarMirror();

        // After the dock is up, run the one-time startup prompts (defer so the dock renders first).
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            PromptAddToStartupIfNeeded();
            CheckAndPromptNewPins();
        });
    }

    // --- Taskbar mirror: live pinned + running apps ---

    private void StartTaskbarMirror()
    {
        RefreshTaskbarApps();
        _appRefreshTimer.Start(); // pick up apps opening/closing

        try
        {
            _pinWatcher = new FileSystemWatcher(TaskbarApps.PinnedFolder, "*.lnk")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            // A new pin (created/renamed .lnk) also triggers a debounced check to offer replication.
            FileSystemEventHandler refresh = (_, _) => Dispatcher.BeginInvoke(() => RefreshTaskbarApps());
            FileSystemEventHandler refreshAndCheck = (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RefreshTaskbarApps();
                _pinCheckTimer.Stop();
                _pinCheckTimer.Start(); // debounce: let the taskband registry catch up before checking
            });
            _pinWatcher.Created += refreshAndCheck;
            _pinWatcher.Deleted += refresh;
            _pinWatcher.Renamed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RefreshTaskbarApps();
                _pinCheckTimer.Stop();
                _pinCheckTimer.Start();
            });
        }
        catch
        {
            // Watching is a nicety; the 1s timer still keeps pins reasonably fresh.
        }
    }

    private void RefreshTaskbarApps() => ViewModel?.RefreshTaskbarApps(_ownProcessId);

    /// <summary>If new shortcuts have been pinned to the taskbar, offer to replicate them on the dock.</summary>
    private void CheckAndPromptNewPins()
    {
        if (ViewModel is null || _promptOpen || !ViewModel.Settings.AskReplicateTaskbarPins)
            return;
        var newPins = ViewModel.FindNewTaskbarPins();
        if (newPins.Count == 0)
            return;

        _promptOpen = true;
        try
        {
            string what = newPins.Count > 1 ? "shortcuts have" : "shortcut has";
            var dialog = new ConfirmDialog($"New {what} been pinned to the taskbar. Would you like to replicate "
                + (newPins.Count > 1 ? "them" : "it") + " on the Dock?") { Owner = this };
            bool replicate = dialog.ShowDialog() == true;

            if (dialog.DoNotAskAgain)
                ViewModel.Settings.AskReplicateTaskbarPins = false;

            if (replicate)
            {
                ViewModel.ReplicateTaskbarPins(newPins); // pins + remembers (saves)
                RefreshTaskbarApps();
            }
            else
            {
                ViewModel.RememberTaskbarPins(newPins); // don't offer these again (saves)
            }
            ViewModel.Save();
        }
        finally
        {
            _promptOpen = false;
        }
    }

    /// <summary>Offers (once) to add Dockable to the Windows startup sequence, unless already there.</summary>
    private void PromptAddToStartupIfNeeded()
    {
        if (ViewModel is null || _promptOpen || !ViewModel.Settings.AskAddToStartup)
            return;
        if (StartupManager.IsEnabled(DockableStartupName)) // already runs at login
            return;

        _promptOpen = true;
        try
        {
            var dialog = new ConfirmDialog("Would you like to add Dockable to startup?") { Owner = this };
            bool add = dialog.ShowDialog() == true;
            if (add)
            {
                string exe = Environment.ProcessPath ?? string.Empty;
                if (!string.IsNullOrEmpty(exe))
                    StartupManager.Enable(DockableStartupName, exe);
                ViewModel.Settings.AskAddToStartup = false; // answered; and IsEnabled will short-circuit anyway
            }
            else if (dialog.DoNotAskAgain)
            {
                ViewModel.Settings.AskAddToStartup = false;
            }
            ViewModel.Save();
        }
        finally
        {
            _promptOpen = false;
        }
    }

    private const string DockableStartupName = "Dockable"; // HKCU Run-key value name (matches Dock Preferences)

    /// <summary>Click a taskbar app: focus its windows if running, otherwise launch it. Minimized
    /// windows (in a thumbnail tile or into this icon) restore with the animation, one after another
    /// (only one effect can play at a time); non-minimized windows are just brought forward.</summary>
    private void ActivateOrLaunch(DockItemViewModel app)
    {
        if (app.Windows.Count == 0)
        {
            ShortcutService.Launch(app.LaunchPath);
            return;
        }

        var toRaise = new List<IntPtr>();
        var toRestore = new List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)>();
        foreach (var hwnd in app.Windows)
        {
            var tile = ViewModel?.FindMinimizedWindow(hwnd);
            if (tile is not null)
                toRestore.Add((hwnd, tile, tile.Icon as BitmapSource));               // minimized as a tile
            else if (WindowControl.IsIconic(hwnd))
                toRestore.Add((hwnd, null, _iconMinimized.GetValueOrDefault(hwnd)
                    ?? _thumbnails.TryGet(hwnd)?.Bitmap));                              // minimized into the icon
            else
                toRaise.Add(hwnd);
        }

        if (toRaise.Count > 0)
            WindowControl.ActivateAll(toRaise);
        RestoreNext(app, toRestore, 0);
    }

    /// <summary>Restores the queued minimized windows one at a time, chaining each animation to the next.</summary>
    private void RestoreNext(DockItemViewModel app, List<(IntPtr Hwnd, DockItemViewModel? Tile, BitmapSource? Bitmap)> queue, int index)
    {
        if (index >= queue.Count)
            return;
        var (hwnd, tile, bitmap) = queue[index];
        Point target = tile is not null ? TileScreenCenter(tile) : TileScreenCenter(app);
        RestoreWindowAnimated(hwnd, tile, target, bitmap, () => RestoreNext(app, queue, index + 1));
    }

    private void OnForegroundChanged()
    {
        UpdateFullscreenState();
        if (StartMenu.IsOpen())
            RaiseDockAboveStartMenu(); // Start menu just came forward — keep the dock above it
    }

    /// <summary>Re-asserts the dock at the top of the topmost band (without stealing focus) so the
    /// Windows Start menu appears behind it. Re-seats the acrylic backdrop just beneath the dock.</summary>
    private void RaiseDockAboveStartMenu()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        PInvoke.SetWindowPos((HWND)_hwnd, new HWND(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        SyncAcrylic();
    }

    /// <summary>
    /// Hides the whole dock (and its acrylic backdrop) while a full-screen app or borderless-fullscreen
    /// game owns the dock's monitor, and restores it when that window goes away — so the dock never
    /// competes with full-screen content.
    /// </summary>
    private void UpdateFullscreenState()
    {
        bool fullscreen = IsForegroundFullscreenOnDockMonitor();
        if (fullscreen == _fullscreenActive)
            return;
        _fullscreenActive = fullscreen;

        if (fullscreen)
        {
            Hide();
            _acrylic.Hide();
        }
        else
        {
            Show();
            PositionDock();
            ApplyGlassEffect();
        }
    }

    /// <summary>True when the foreground window covers the dock's monitor (full-screen), excluding the
    /// desktop/shell and our own windows.</summary>
    private unsafe bool IsForegroundFullscreenOnDockMonitor()
    {
        if (_hwnd == IntPtr.Zero)
            return false;

        HWND fg = PInvoke.GetForegroundWindow();
        if (fg.IsNull || fg == (HWND)_hwnd || fg == PInvoke.GetDesktopWindow() || fg == PInvoke.GetShellWindow())
            return false;

        uint pid = 0;
        PInvoke.GetWindowThreadProcessId(fg, &pid);
        if (pid == _ownProcessId) // our backdrop / overlays / popups
            return false;

        // Only clear the way if the full-screen window is on the dock's own monitor.
        var fgMon = PInvoke.MonitorFromWindow(fg, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var dockMon = PInvoke.MonitorFromWindow((HWND)_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (fgMon != dockMon)
            return false;

        if (!PInvoke.GetWindowRect(fg, out RECT r))
            return false;
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(fgMon, ref mi))
            return false;
        var m = mi.rcMonitor;
        return r.left <= m.left && r.top <= m.top && r.right >= m.right && r.bottom >= m.bottom;
    }

    private void ApplyTaskbarVisibility()
    {
        if (ViewModel is null)
            return;

        // Native auto-hide: the taskbar slides away but reveals when the cursor reaches the
        // bottom edge. Self-restoring — a force-kill leaves it usable, so no watchdog is needed.
        Taskbar.SetAutoHide(ViewModel.Settings.HideTaskbar);
    }

    /// <summary>Anchors the dock flush to its monitor's docked edge, centered along the other axis.</summary>
    private void PositionDock()
    {
        var (left, top) = ComputePlacement();
        Left = left;
        Top = top;
        SyncAcrylic();
    }

    /// <summary>Shows/hides and configures the acrylic backdrop for the selected Glass Effect: Simple
    /// hides it (the bar keeps its plain translucent brush); Acrylic/Liquid Glass show it with the
    /// matching backdrop brush.</summary>
    private void ApplyGlassEffect()
    {
        var mode = ViewModel?.Settings.GlassEffect ?? GlassEffect.Acrylic;
        if (mode == GlassEffect.Simple)
        {
            _acrylic.Hide();
            return;
        }
        _acrylic.SetEffect(mode);
        _acrylic.Show();
        SyncAcrylic();
    }

    private void SetGlassEffect(GlassEffect mode)
    {
        if (ViewModel is null || ViewModel.Settings.GlassEffect == mode)
            return;
        ViewModel.Settings.GlassEffect = mode;
        ViewModel.Save();
        ApplyGlassEffect();
    }

    /// <summary>Tracks the acrylic backdrop window to the bar's current screen rect (physical px) and
    /// keeps it z-ordered just below the dock. Called whenever the bar moves or resizes.</summary>
    private void SyncAcrylic()
    {
        if (ViewModel is null || _hwnd == IntPtr.Zero || ViewModel.BarWidth <= 0 || ViewModel.BarHeight <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var topLeft = PointToScreen(new Point(ViewModel.BarLeft, ViewModel.BarTop)); // device pixels
        int w = (int)Math.Round(ViewModel.BarWidth * dpi.DpiScaleX);
        int h = (int)Math.Round(ViewModel.BarHeight * dpi.DpiScaleY);
        float corner = (float)(BarCornerRadius * dpi.DpiScaleX);
        _acrylic.SetBounds((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), w, h, corner, _hwnd);
    }

    private (double Left, double Top) ComputePlacement()
    {
        double height = ActualHeight > 0 ? ActualHeight : (ViewModel?.WindowHeight ?? 0);
        double width = ActualWidth > 0 ? ActualWidth : (ViewModel?.WindowWidth ?? 0);
        var edge = ViewModel?.Settings.Edge ?? DockEdge.Bottom;

        // Monitor bounds (DIP) for the screen the dock currently sits on.
        double mLeft, mTop, mWidth, mHeight;
        if (_hwnd != IntPtr.Zero)
        {
            var info = Monitors.ForWindow(_hwnd);
            double scale = info.Scale;
            mLeft = info.MonitorPx.Left / scale;
            mTop = info.MonitorPx.Top / scale;
            mWidth = info.MonitorPx.Width / scale;
            mHeight = info.MonitorPx.Height / scale;
        }
        else // before the HWND exists: fall back to the primary screen
        {
            mLeft = 0;
            mTop = 0;
            mWidth = SystemParameters.PrimaryScreenWidth;
            mHeight = SystemParameters.PrimaryScreenHeight;
        }

        // Anchor flush to the docked edge; center along the perpendicular axis. The AppBar reserves
        // the strip so other windows don't overlap (when the taskbar is hidden the work area is full,
        // so the monitor edge is the freed space).
        return edge switch
        {
            DockEdge.Top => (mLeft + (mWidth - width) / 2, mTop),
            DockEdge.Left => (mLeft, mTop + (mHeight - height) / 2),
            DockEdge.Right => (mLeft + mWidth - width, mTop + (mHeight - height) / 2),
            _ => (mLeft + (mWidth - width) / 2, mTop + mHeight - height), // Bottom
        };
    }

    // --- Magnification render loop ---

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizePressed || _separatorResize)
        {
            _hovering = true; // keep the dock revealed while resizing
            var sp = PointToScreen(e.GetPosition(this));
            HandleSeparatorResize(IsVerticalDock ? sp.X : sp.Y); // resize is along the cross axis
            return;
        }

        var p = e.GetPosition(this);
        _mouseX = p.X;
        _mouseY = p.Y;
        _lastCursor = e.GetPosition(RootCanvas);
        _hovering = true;
        HookRendering();

        if (_dragInitiated)
        {
            PositionGhost(_lastCursor);
            // Real motion cancels a pending/active "Remove" and restarts the hold countdown.
            if (Distance(_lastCursor, _steadyAnchor) > SteadyEpsilon)
            {
                _steadyAnchor = _lastCursor;
                if (_removeArmed)
                    DisarmRemove();
                RestartSteady();
            }
        }
        else
        {
            MaybeStartDrag(e);
        }
    }

    private void MaybeStartDrag(MouseEventArgs e)
    {
        if (_dragCandidate is null || _dragInitiated || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        StartDrag();
    }

    // Begins the custom (non-modal) drag: lift the in-canvas tile into a gap and show the
    // free-roaming ghost popup. Used both on movement and on a 500ms long-press.
    private void StartDrag()
    {
        if (_dragCandidate is null)
            return;

        _dragInitiated = true;
        ViewModel?.BeginItemDrag(_dragCandidate);
        CaptureMouse();

        GhostIcon.Source = _dragCandidate.Icon;
        GhostRemoveTag.Visibility = Visibility.Hidden;
        GhostRoot.BeginAnimation(OpacityProperty, null); // drop any leftover fade-out hold
        GhostRoot.Opacity = 1;
        DragGhost.IsOpen = true;
        PositionGhost(_lastCursor);

        _steadyAnchor = _lastCursor;
        RestartSteady();
        HookRendering();
    }

    // After DragSteadyMs of stillness: if the gesture hasn't lifted yet (pure long-press),
    // start it now; then arm "Remove" for pinned shortcuts.
    private void OnDragSteadyElapsed(object? sender, EventArgs e)
    {
        _dragSteadyTimer.Stop();
        if (_dragCandidate is null)
            return;
        if (!_dragInitiated)
            StartDrag();
        if (IsRemovable(_dragCandidate))
            ArmRemove();
    }

    private void RestartSteady()
    {
        _dragSteadyTimer.Stop();
        if (IsRemovable(_dragCandidate)) // only pinned shortcuts can be removed
            _dragSteadyTimer.Start();
    }

    private void ArmRemove()
    {
        _removeArmed = true;
        GhostRemoveTag.Visibility = Visibility.Visible;
    }

    private void DisarmRemove()
    {
        _removeArmed = false;
        GhostRemoveTag.Visibility = Visibility.Hidden;
    }

    private void PositionGhost(Point cursorInCanvas)
    {
        DragGhost.HorizontalOffset = cursorInCanvas.X - GhostCenterX;
        DragGhost.VerticalOffset = cursorInCanvas.Y - GhostCenterY;
    }

    // Fades the ghost out, then closes it. A slightly longer fade reads as a "poof" on remove.
    private void EndGhost(bool poof)
    {
        if (!DragGhost.IsOpen)
            return;
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(poof ? 180 : 130)));
        fade.Completed += (_, _) =>
        {
            DragGhost.IsOpen = false;
            GhostRoot.BeginAnimation(OpacityProperty, null); // release the hold so Opacity sticks
            GhostRoot.Opacity = 1;
            GhostRemoveTag.Visibility = Visibility.Hidden;
        };
        GhostRoot.BeginAnimation(OpacityProperty, fade);
    }

    private static bool IsRemovable(DockItemViewModel? item) => item is { IsTaskbarApp: true, IsPinned: true };

    // --- Separator drag = resize the dock (the Size setting) ---

    // screenCross is the cursor's screen coord on the cross axis in device px (Y for a horizontal
    // dock, X for a vertical one). PointToScreen stays correct even as the dock window moves while
    // growing. The dock grows when dragging away from the docked edge (into the depth).
    private void HandleSeparatorResize(double screenCross)
    {
        if (ViewModel is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        double dpiScale = IsVerticalDock ? dpi.DpiScaleX : dpi.DpiScaleY;
        double rawDelta = ViewModel.Settings.Edge switch
        {
            DockEdge.Top => screenCross - _resizeStartCursorCross,  // drag down = larger
            DockEdge.Left => screenCross - _resizeStartCursorCross, // drag right = larger
            DockEdge.Right => _resizeStartCursorCross - screenCross, // drag left = larger
            _ => _resizeStartCursorCross - screenCross,             // Bottom: drag up = larger
        };
        double deltaDip = rawDelta / dpiScale;

        if (!_separatorResize)
        {
            if (Math.Abs(deltaDip) < SystemParameters.MinimumVerticalDragDistance)
                return; // not a drag yet
            _separatorResize = true;
            _resizePressed = false;
            _hoveredItem = null;       // suppress the hover label during a resize
            CaptureMouse();
            Cursor = IsVerticalDock ? Cursors.SizeWE : Cursors.SizeNS;
        }

        double newSize = Math.Round(Math.Clamp(_resizeStartIconSize + deltaDip, SizeMin, SizeMax));
        if (newSize != ViewModel.Settings.IconSize)
        {
            ViewModel.Settings.IconSize = newSize;
            ViewModel.RecomputeLayout();            // resize the dock live
            _settingsWindow?.SyncSizeFromSettings(); // keep the Dock Preferences slider in sync
        }
    }

    private void EndSeparatorResize()
    {
        bool wasResizing = _separatorResize;
        _separatorResize = false;
        _resizePressed = false;
        if (wasResizing)
        {
            ReleaseMouseCapture();
            Cursor = null;
            ViewModel?.Save(); // persist the new Size
        }
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void OnDockMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_separatorResize || _resizePressed)
        {
            EndSeparatorResize();
            e.Handled = true;
            return;
        }

        _dragSteadyTimer.Stop();
        if (!_dragInitiated)
        {
            _dragCandidate = null;
            return;
        }

        var item = _dragCandidate;
        bool armed = _removeArmed;
        _dragInitiated = false;     // clear before releasing capture so OnLostMouseCapture no-ops
        ReleaseMouseCapture();

        if (ViewModel is not null && item is not null)
        {
            bool removable = IsRemovable(item);
            bool unpinnedApp = item is { IsTaskbarApp: true, IsPinned: false };
            var pos = e.GetPosition(RootCanvas);
            bool overDock = pos.X >= 0 && pos.Y >= 0 && pos.X <= ActualWidth && pos.Y <= ActualHeight;

            if (armed && removable)
                ViewModel.UnpinApp(item.LaunchPath);                            // hold-to-Remove
            else if (removable && overDock)
                ViewModel.MovePin(item.LaunchPath, ViewModel.DragInsertIndex);  // reorder pins
            else if (unpinnedApp && overDock)
                ViewModel.PinApp(item.LaunchPath, ViewModel.DragInsertIndex, item.DisplayName); // pin a running app where dropped (keep its open name)
            // Otherwise (minimized window, or dropped away): no change → snaps back.

            ViewModel.EndItemDrag();    // the in-canvas tile reappears and settles to its slot
            RefreshTaskbarApps();
        }

        EndGhost(poof: armed);
        _dragCandidate = null;
        _removeArmed = false;
        e.Handled = true;
    }

    // A drag can be interrupted (Alt+Tab, another app grabs capture). Tidy up like a snap-back.
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_separatorResize)
        {
            EndSeparatorResize();
            return;
        }
        if (!_dragInitiated)
            return; // a normal release already handled things
        _dragInitiated = false;
        _dragSteadyTimer.Stop();
        ViewModel?.EndItemDrag();
        RefreshTaskbarApps();
        EndGhost(poof: false);
        _dragCandidate = null;
        _removeArmed = false;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _hovering = true;
        ClearWindowRegion(); // un-clip so magnified icons can render above the bar and stay clickable
        HookRendering();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        // Keep the loop running so the dock eases back to rest, then it self-detaches.
        _hovering = false;
        _hoveredItem = null; // hide the hover label once the cursor leaves the dock
    }

    // --- Docking behavior (always-visible: reserve a strip via the AppBar) ---

    private void ApplyBehavior()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;

        // Reserve a strip so other windows don't overlap the resting bar.
        ReserveAppBarSpace();
        PositionDock();
        _appBar?.NotifyPosChanged();

        ApplyIdleRegion(); // clip to the resting bar so the overflow area stays click-through
    }

    private void ReserveAppBarSpace()
    {
        if (_appBar is null || ViewModel is null)
            return;

        // The reserved strip's thickness is the bar's cross dimension: its height for a horizontal
        // dock, its width for a vertical one.
        double thicknessDip = ViewModel.IsVerticalDock ? ViewModel.BarWidth : ViewModel.BarHeight;
        if (thicknessDip <= 0)
            thicknessDip = 64;
        var info = Monitors.ForWindow(_hwnd);
        int thicknessPx = (int)Math.Round(thicknessDip * info.Scale);

        _appBar.Register();
        _appBar.ReserveEdge(ViewModel.Settings.Edge, info.MonitorPx, thicknessPx);
    }

    /// <summary>
    /// When the dock is idle, clips the window down to the resting bar so the magnification-overflow
    /// area above it is click-through (the AppBar only reserves the resting bar; the taller window
    /// must not block windows underneath). Cleared while hovering so the magnified icons render above
    /// the bar and stay clickable.
    /// </summary>
    private void ApplyIdleRegion()
    {
        if (_hwnd == IntPtr.Zero || ViewModel is null)
            return;
        if (_hovering || ViewModel.WindowWidth <= 0 || ViewModel.WindowHeight <= 0)
        {
            ClearWindowRegion();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        double pad = DockRegionTopPaddingDip; // keep the bar's drop shadow
        // Keep the strip from the screen edge through the bar (+shadow pad); clip the magnification
        // overflow that bleeds away from the docked edge so it stays click-through.
        double l = 0, t = 0, r = ViewModel.WindowWidth, b = ViewModel.WindowHeight;
        switch (ViewModel.Settings.Edge)
        {
            case DockEdge.Top: b = ViewModel.BarTop + ViewModel.BarHeight + pad; break;
            case DockEdge.Left: r = ViewModel.BarLeft + ViewModel.BarWidth + pad; break;
            case DockEdge.Right: l = Math.Max(0, ViewModel.BarLeft - pad); break;
            default: t = Math.Max(0, ViewModel.BarTop - pad); break; // Bottom
        }

        int left = (int)Math.Floor(l * dpi.DpiScaleX);
        int top = (int)Math.Floor(t * dpi.DpiScaleY);
        int right = (int)Math.Ceiling(r * dpi.DpiScaleX) + 1;
        int bottom = (int)Math.Ceiling(b * dpi.DpiScaleY) + 1;

        var region = PInvoke.CreateRectRgn(left, top, right, bottom);
        PInvoke.SetWindowRgn((HWND)_hwnd, region, true); // the window takes ownership of the region
        _windowRegionClipped = true;
    }

    /// <summary>Removes any window-region clip so the full window is rendered and hit-testable.</summary>
    private void ClearWindowRegion()
    {
        if (_hwnd == IntPtr.Zero || !_windowRegionClipped)
            return;
        PInvoke.SetWindowRgn((HWND)_hwnd, default, true);
        _windowRegionClipped = false;
    }

    private void SetEdge(DockEdge edge)
    {
        if (ViewModel is null || ViewModel.Settings.Edge == edge)
            return;
        ViewModel.ApplyEdge(edge); // persist, re-lay out, and notify edge-derived view bindings
        ApplyBehavior();           // move the AppBar reservation, reposition, and re-clip
    }

    private void SetHideTaskbar(bool hide)
    {
        if (ViewModel is null || ViewModel.Settings.HideTaskbar == hide)
            return;
        ViewModel.Settings.HideTaskbar = hide;
        ViewModel.Save();
        ApplyTaskbarVisibility();
        PositionDock(); // bottom reference changed (work area vs full screen)
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // The OS light/dark setting changed — re-theme if we're following the system.
        if (msg == WM_SETTINGCHANGE
            && ViewModel?.Settings.Theme == DockTheme.System
            && Marshal.PtrToStringAuto(lParam) == "ImmersiveColorSet")
        {
            ApplyTheme();
        }

        if ((uint)msg == AppBarCallbackMessage)
        {
            switch ((uint)wParam.ToInt64())
            {
                case PInvoke.ABN_POSCHANGED:
                    // The taskbar or another appbar moved; re-reserve and reposition.
                    ReserveAppBarSpace();
                    PositionDock();
                    handled = true;
                    break;
                case PInvoke.ABN_FULLSCREENAPP:
                    UpdateFullscreenState(); // a full-screen app opened/closed — clear or restore the dock
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // --- Minimize → dock tile, and tile click → restore (genie or scale per the setting) ---

    /// <summary>The minimize/restore animator selected by <see cref="MinimizeEffect"/>. Suck and Genie
    /// share the mesh animator, differing only in its <see cref="GenieAnimator.Style"/> curve.</summary>
    private IMinimizeAnimator MinimizeAnimator
    {
        get
        {
            var effect = ViewModel?.Settings.MinimizeEffect ?? MinimizeEffect.Genie;
            double speed = ViewModel?.Settings.EffectSpeed ?? 1.0;
            if (effect == MinimizeEffect.Scale)
            {
                _scale.SpeedMultiplier = speed;
                return _scale;
            }
            _genie.Style = effect == MinimizeEffect.Genie
                ? GenieAnimator.GenieStyle.Genie
                : GenieAnimator.GenieStyle.Suck;
            _genie.SpeedMultiplier = speed;
            return _genie;
        }
    }

    /// <summary>A real window started minimizing: capture it, then warp it into its app icon (when
    /// "minimize into icon" is on and the app has a dock icon) or into a new thumbnail tile.</summary>
    private void OnWindowMinimizing(IntPtr hwnd)
    {
        if (ViewModel is null || _busy.Contains(hwnd) || ViewModel.FindMinimizedWindow(hwnd) is not null
            || _iconMinimized.ContainsKey(hwnd))
            return;
        _busy.Add(hwnd);

        // Use the capture taken while the window was still visible; capturing now (it's
        // already minimizing) would grab a tiny black sliver.
        var capture = _thumbnails.TryGet(hwnd) ?? WindowCapture.Capture(hwnd);
        WindowControl.SuppressTransitions(hwnd); // future restore won't play the OS animation

        if (capture is null)
        {
            _busy.Remove(hwnd);
            return;
        }

        var info = Monitors.ForWindow(hwnd);
        var sourceDip = ToDip(capture.Value.ScreenRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);

        var animator = MinimizeAnimator;
        var bitmap = capture.Value.Bitmap;

        // "Minimize into icon": warp into the app's dock icon (pinned or running) instead of a separate
        // thumbnail tile. Only when an app icon actually exists for this window — otherwise fall back
        // to a thumbnail tile (resolved in the deferred step below).
        var appTile = ViewModel.Settings.MinimizeIntoIcon ? ViewModel.FindAppForWindow(hwnd) : null;

        // The minimize-start event reaches us only AFTER the OS has already minimized the window, and
        // transitions are suppressed, so it vanished instantly (no OS scale animation). Don't restore
        // it — just paint the captured frame at the window's old spot and warp it into the dock right
        // away: a single minimize, no restore/re-minimize dance.
        animator.ShowAtSource(bitmap, sourceDip, monitorDip);

        Point target;
        if (appTile is not null)
        {
            // Into the app icon: remember the capture so the icon click can warp it back out.
            _iconMinimized[hwnd] = bitmap;
            target = TileScreenCenter(appTile);
        }
        else
        {
            var tile = ViewModel.AddMinimizedWindow(hwnd, bitmap, TaskbarApps.GetWindowTitle(hwnd));
            LoadOverlayIcon(tile, hwnd);
            target = TileScreenCenter(tile);
        }
        animator.AnimateTo(target, reverse: false, onCompleted: () => _busy.Remove(hwnd));
    }

    /// <summary>Loads the app icon for a minimized tile and badges it onto the thumbnail.</summary>
    private static async void LoadOverlayIcon(DockItemViewModel tile, IntPtr hwnd)
    {
        string exe = TaskbarApps.GetWindowExePath(hwnd);
        if (string.IsNullOrEmpty(exe))
            return;
        tile.OverlayIcon = await ShortcutService.LoadIconAsync(exe, 64);
    }

    /// <summary>A minimized-window tile was clicked: reverse-warp out and restore the window.</summary>
    private void RestoreMinimized(DockItemViewModel tile)
        => RestoreWindowAnimated(tile.Hwnd, tile, TileScreenCenter(tile), tile.Icon as BitmapSource, static () => { });

    /// <summary>
    /// Restores one minimized window, reverse-warping out of <paramref name="target"/> (its tile or
    /// app icon), then runs <paramref name="onDone"/> (used to chain a sequential group restore).
    /// Falls back to a plain restore when there's no captured bitmap to animate.
    /// </summary>
    private void RestoreWindowAnimated(IntPtr hwnd, DockItemViewModel? tile, Point target, BitmapSource? bitmap, Action onDone)
    {
        if (ViewModel is null || _busy.Contains(hwnd))
        {
            onDone();
            return;
        }

        if (!WindowControl.IsWindow(hwnd))
        {
            // Window is gone; drop any stale tile / tracking and move on.
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            onDone();
            return;
        }

        if (bitmap is null)
        {
            WindowControl.Restore(hwnd);
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            onDone();
            return;
        }

        _busy.Add(hwnd);
        WindowControl.SuppressTransitions(hwnd);

        var info = Monitors.ForWindow(hwnd);
        var restoreRectPx = WindowControl.GetRestoreRect(hwnd) ?? new Int32Rect(0, 0, 600, 400);
        var windowDip = ToDip(restoreRectPx, info.Scale);
        var monitorDip = ToDip(info.MonitorPx, info.Scale);

        MinimizeAnimator.Play(bitmap, windowDip, target, monitorDip, reverse: true, onCompleted: () =>
        {
            WindowControl.Restore(hwnd);
            if (tile is not null)
                ViewModel.RemoveMinimizedWindow(tile);
            _iconMinimized.Remove(hwnd);
            _busy.Remove(hwnd);
            onDone();
        });
    }

    private static Rect ToDip(Int32Rect r, double scale)
        => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);

    private static Rect ToDip(Rect r, double scale)
        => new(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

    /// <summary>Screen-space (DIP) center of a tile.</summary>
    private Point TileScreenCenter(DockItemViewModel tile)
    {
        var (left, top) = ComputePlacement();
        return new Point(left + tile.X + tile.RenderSize / 2, top + tile.Y + tile.RenderSize / 2);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Suppress magnification while resizing via a separator (icons stay at the new resting size).
        double mouseMain = IsVerticalDock ? _mouseY : _mouseX;
        bool animating = ViewModel?.UpdateMagnification(mouseMain, _hovering && !_separatorResize) ?? false;
        SyncAcrylic();      // track the acrylic backdrop to the (magnifying) bar each frame
        UpdateHoverLabel(); // track the hovered icon's live center each frame

        // A shrunk-out (departed) item is removed off the render pass to avoid mutating Items mid-frame.
        if (ViewModel?.HasFinishedDeparting == true && !_finalizeScheduled)
        {
            _finalizeScheduled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _finalizeScheduled = false;
                ViewModel?.FinalizeDeparted();
            });
        }

        if (!animating && !_hovering)
        {
            UnhookRendering();
            ApplyIdleRegion(); // settled at rest → clip back to the bar so the overflow is click-through
        }
    }

    private void HookRendering()
    {
        if (_renderingHooked)
            return;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked)
            return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    // --- Item interaction ---

    private void DockItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragInitiated = false;
        _removeArmed = false;
        var item = (sender as FrameworkElement)?.DataContext as DockItemViewModel;

        // Dragging a separator up/down resizes the dock (the Size setting).
        if (item is { IsSeparator: true } && ViewModel is not null)
        {
            _resizePressed = true;
            var sp = PointToScreen(e.GetPosition(this));
            _resizeStartCursorCross = IsVerticalDock ? sp.X : sp.Y;
            _resizeStartIconSize = ViewModel.Settings.IconSize;
            _dragCandidate = null;
            return;
        }

        // Taskbar apps (pinned or running) and minimized-window tiles can be dragged.
        _dragCandidate = item is not null && (item.IsTaskbarApp || item.IsMinimizedWindow) ? item : null;
        _dragStart = e.GetPosition(this);
        _lastCursor = e.GetPosition(RootCanvas);
        _steadyAnchor = _lastCursor;
        RestartSteady(); // arms the hold-to-Remove countdown for pinned shortcuts only
    }

    // --- Shared hover label ---

    // Persist the hovered item across the small gaps between icons; it's cleared when the cursor
    // leaves the dock (OnMouseLeave) or moves onto another labelled icon.
    private void DockItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockItemViewModel item } && item.ShowLabel)
            _hoveredItem = item;
    }

    // Positioned every frame from the render loop: centered on the hovered icon's live center, with
    // the arrow just above the icon's fully-magnified top so it never overlaps as magnification grows.
    private void UpdateHoverLabel()
    {
        var item = _hoveredItem;
        // The label popup (downward arrow, MagnifiedTop anchor) is tuned for the Bottom edge; suppress
        // it on the other edges for now rather than render it in the wrong place. TODO: per-edge labels.
        if (item is null || _dragInitiated || _separatorResize || ViewModel is null || !item.ShowLabel
            || ViewModel.Settings.Edge != DockEdge.Bottom)
        {
            if (LabelPopup.IsOpen)
                LabelPopup.IsOpen = false;
            _labelItem = null;
            return;
        }

        if (!ReferenceEquals(_labelItem, item))
        {
            LabelText.Text = item.DisplayName;
            _labelItem = item;
        }
        if (!LabelPopup.IsOpen)
            LabelPopup.IsOpen = true;

        // Use the content's real laid-out size (measuring a not-yet-open popup child is unreliable
        // and gave a roughly-constant width — the cause of the off-center, entry-direction-dependent
        // placement). ActualWidth is valid once the popup has laid out; fall back to a measure only
        // for the first frame after opening.
        var content = (FrameworkElement)LabelPopup.Child;
        double w = content.ActualWidth, h = content.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            w = content.DesiredSize.Width;
            h = content.DesiredSize.Height;
        }

        double centerX = item.X + item.RenderWidth / 2;          // hovered icon's live center
        double arrowTipY = ViewModel.MagnifiedTop - LabelGap;    // just above a fully-magnified icon
        LabelPopup.HorizontalOffset = centerX - w / 2;
        LabelPopup.VerticalOffset = arrowTipY - h;
    }

    // The popup is its own top-level window; WS_EX_TRANSPARENT lets the cursor fall through to the
    // icon beneath, so the label never competes for hover (which caused magnify jitter/flicker).
    private void LabelPopup_Opened(object? sender, EventArgs e)
    {
        if (sender is Popup { Child: { } child } && PresentationSource.FromVisual(child) is HwndSource source)
        {
            var hwnd = (HWND)source.Handle;
            var ex = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            ex |= WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)(uint)ex);
        }
    }

    private void DockItem_Click(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (_dragInitiated) // this gesture was a drag, not a click
        {
            _dragInitiated = false;
            return;
        }

        if (sender is not FrameworkElement { DataContext: DockItemViewModel item })
            return;

        if (item.IsMinimizedWindow)
            RestoreMinimized(item);
        else if (item.IsTaskbarApp)
            ActivateOrLaunch(item);
        else if (item.IsStartMenu)
            OpenStartMenu();
        else
            item.Activate(); // shortcut / Recycle Bin
    }

    /// <summary>
    /// Opens the Start menu from the dock and fully hides the taskbar while it's up — overriding
    /// auto-hide so the taskbar can't reveal itself alongside Start. The configured taskbar state is
    /// restored once Start closes (watched by <see cref="OnStartWatchTick"/>).
    /// </summary>
    private void OpenStartMenu()
    {
        Taskbar.Hide();   // override auto-hide: keep the taskbar fully hidden while Start is open
        StartMenu.Open(); // synthesize the Win key
        _startSeen = false;
        _startWatchTicks = 0;
        _startWatchTimer.Start();
    }

    private void OnStartWatchTick(object? sender, EventArgs e)
    {
        _startWatchTicks++;
        if (StartMenu.IsOpen())
        {
            _startSeen = true;            // Start is up — keep the taskbar hidden
            RaiseDockAboveStartMenu();    // ...and keep the dock in front, so Start sits behind it
            return;
        }

        // Start isn't showing. Restore once we've actually seen it open (the user dismissed it), or
        // after the grace period if it never appeared, so the taskbar is never left stuck hidden.
        if (_startSeen || _startWatchTicks >= StartWatchMaxTicks)
        {
            _startWatchTimer.Stop();
            ApplyTaskbarVisibility(); // back to the configured auto-hide / visible state
        }
    }

    // --- Per-item right-click context menus (built in code for live state / Alt handling) ---

    private void DockItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockItemViewModel item } target || ViewModel is null)
            return;

        // Consume the right-click on the item so it doesn't fall through to the empty-space dock menu
        // (items without their own menu — Start / Recycle Bin / minimized tiles — simply show nothing).
        e.Handled = true;

        ContextMenu? menu = item switch
        {
            { IsTaskbarApp: true } => BuildAppMenu(item),
            { IsSeparator: true } => BuildSeparatorMenu(),
            _ => null,
        };
        if (menu is null)
            return;

        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    // Right-clicking empty space inside the dock (the bar background) shows the dock-wide menu.
    private void Dock_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel is null)
            return;
        var menu = BuildSeparatorMenu();
        menu.PlacementTarget = this;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // Separator menu: dock-wide actions (matches the old shared menu).
    private ContextMenu BuildSeparatorMenu()
    {
        var menu = new ContextMenu();
        var prefs = new MenuItem { Header = "Dock Preferences…" };
        prefs.Click += (_, _) => OpenDockPreferences();
        menu.Items.Add(prefs);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "Quit Dockable" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);
        return menu;
    }

    // Open/pinned app menu: Options ▶ (Keep in Dock / Open at Login / Show in Explorer), then — for
    // running apps — Show All Windows and Quit (Force Quit while Alt is held).
    private ContextMenu BuildAppMenu(DockItemViewModel app)
    {
        var menu = new ContextMenu();

        // New Window: launch another instance of the app (most apps open a fresh window).
        var newWindow = new MenuItem { Header = "New Window" };
        newWindow.Click += (_, _) => ShortcutService.Launch(app.LaunchPath);
        menu.Items.Add(newWindow);

        // Rename: change a pinned shortcut's display label (persisted via PinNames).
        if (app.IsPinned)
        {
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) => RenamePin(app);
            menu.Items.Add(rename);
        }

        menu.Items.Add(new Separator());

        var options = new MenuItem { Header = "Options" };

        var keep = new MenuItem { Header = "Keep in Dock", IsCheckable = true, IsChecked = app.IsPinned };
        keep.Click += (_, _) =>
        {
            if (app.IsPinned)
                ViewModel!.UnpinApp(app.LaunchPath);
            else
                ViewModel!.PinApp(app.LaunchPath, int.MaxValue, app.DisplayName); // append to the pinned list (keep its name)
            RefreshTaskbarApps();
        };
        options.Items.Add(keep);

        string exe = ResolveExecutable(app);
        string startupName = StartupEntryName(app, exe);
        var login = new MenuItem { Header = "Open at Login", IsCheckable = true, IsChecked = StartupManager.IsEnabled(startupName) };
        login.Click += (_, _) =>
        {
            if (StartupManager.IsEnabled(startupName))
                StartupManager.Disable(startupName);
            else
                StartupManager.Enable(startupName, exe);
        };
        options.Items.Add(login);

        var reveal = new MenuItem { Header = "Show in Explorer" };
        reveal.Click += (_, _) => ShortcutService.RevealInExplorer(app.LaunchPath);
        options.Items.Add(reveal);

        menu.Items.Add(options);

        // Running-app actions only make sense when the app has open windows.
        if (app.Windows.Count > 0)
        {
            menu.Items.Add(new Separator());

            var showAll = new MenuItem { Header = "Show All Windows" };
            showAll.Click += (_, _) => ActivateOrLaunch(app);
            menu.Items.Add(showAll);

            var quit = new MenuItem();
            SetQuitHeader(quit, AltHeld);
            quit.Click += (_, _) => QuitApp(app, force: AltHeld);
            menu.Items.Add(quit);

            // Live-toggle the Quit / Force Quit label as Alt is pressed/released with the menu open.
            menu.PreviewKeyDown += (_, _) => SetQuitHeader(quit, AltHeld);
            menu.PreviewKeyUp += (_, _) => SetQuitHeader(quit, AltHeld);
        }

        return menu;
    }

    /// <summary>Prompts for a new display label for a pinned shortcut and applies it.</summary>
    private void RenamePin(DockItemViewModel app)
    {
        var dialog = new InputDialog("Rename this shortcut:", app.DisplayName) { Owner = this };
        if (dialog.ShowDialog() == true)
            ViewModel?.RenamePin(app.LaunchPath, dialog.Value);
    }

    private static bool AltHeld => (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

    private static void SetQuitHeader(MenuItem item, bool force) => item.Header = force ? "Force Quit" : "Quit";

    /// <summary>The app's executable path (from a running window if possible, else its launch path).</summary>
    private static string ResolveExecutable(DockItemViewModel app)
    {
        if (app.Windows.Count > 0)
        {
            string exe = TaskbarApps.GetWindowExePath(app.Windows[0]);
            if (!string.IsNullOrEmpty(exe))
                return exe;
        }
        return app.LaunchPath;
    }

    /// <summary>A stable HKCU Run value name for the app (its executable file name, else display name).</summary>
    private static string StartupEntryName(DockItemViewModel app, string exe)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(exe);
            if (!string.IsNullOrEmpty(name))
                return name;
        }
        catch { /* fall through */ }
        return app.DisplayName;
    }

    /// <summary>Gracefully closes (or, when forced, kills) every window/process backing the app.</summary>
    private void QuitApp(DockItemViewModel app, bool force)
    {
        if (!force)
        {
            foreach (var hwnd in app.Windows.ToArray())
                WindowControl.Close(hwnd); // WM_CLOSE — lets the app prompt to save
            return;
        }

        var pids = new HashSet<uint>();
        foreach (var hwnd in app.Windows)
        {
            uint pid = WindowControl.GetProcessId(hwnd);
            if (pid != 0)
                pids.Add(pid);
        }
        foreach (uint pid in pids)
        {
            try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); }
            catch { /* already gone / no access */ }
        }
    }

    /// <summary>Shows the "Dock Preferences" window, focusing it if already open (single instance).</summary>
    private void OpenDockPreferences()
    {
        if (ViewModel is null)
            return;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(ViewModel, SetTheme, SetEdge, SetHideTaskbar, SetGlassEffect);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // --- Drag to reorder / pin ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            ClearExternalDropGap();
            e.Handled = true;
            return;
        }

        double main = DropMain(e.GetPosition(this));
        if (IsOverRecycleBin(main))
        {
            e.Effects = DragDropEffects.Move;
            ClearExternalDropGap(); // no placeholder when hovering the Recycle Bin
        }
        else
        {
            e.Effects = DragDropEffects.Copy;
            ShowExternalDropGap(main); // part the tiles to preview where the icon would land
        }
        e.Handled = true;
    }

    // The external drag left the dock without dropping: close the placeholder gap.
    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ClearExternalDropGap();
        e.Handled = true;
    }

    // External files dragged from Explorer (internal reorder uses the custom mouse drag above).
    private void OnDrop(object sender, DragEventArgs e)
    {
        ClearExternalDropGap();
        if (ViewModel is null || e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        double main = DropMain(e.GetPosition(this));

        // Dropped onto the Recycle Bin → move them there; anywhere else → pin them where the gap was.
        if (IsOverRecycleBin(main))
        {
            RecycleBin.SendToRecycleBin(paths);
            RefreshTaskbarApps(); // refresh the bin's empty/full icon promptly
            return;
        }

        int index = ViewModel.ComputeDropIndex(main);
        foreach (var path in paths)
            // Pin a .lnk's destination, not the .lnk — but keep the shortcut's name (e.g. "Chrome.lnk" → "Chrome").
            ViewModel.PinApp(TaskbarApps.ResolveToTarget(path), index++, System.IO.Path.GetFileNameWithoutExtension(path));

        RefreshTaskbarApps();
    }

    // Opens / refreshes the placeholder gap and keeps the render loop running so the tiles part and
    // track the cursor; cleared on leave/drop so they glide back together.
    private void ShowExternalDropGap(double main)
    {
        ClearWindowRegion(); // unclip so the widening bar and parted tiles render (and receive the drag)
        ViewModel?.UpdateExternalDrop(main);
        HookRendering();
    }

    private void ClearExternalDropGap()
    {
        ViewModel?.EndExternalDrop();
        HookRendering(); // run the loop so the parted tiles settle back
    }

    /// <summary>The cursor's main-axis (window) coordinate: X for a horizontal dock, Y for a vertical one.</summary>
    private double DropMain(Point p) => IsVerticalDock ? p.Y : p.X;

    /// <summary>Main-axis position/size of an item along the bar (handles both orientations).</summary>
    private (double Pos, double Size) ItemMain(DockItemViewModel item) =>
        IsVerticalDock ? (item.Y, item.RenderSize) : (item.X, item.RenderWidth);

    /// <summary>True if the given main-axis coordinate falls within the Recycle Bin tile.</summary>
    private bool IsOverRecycleBin(double cursorMain)
    {
        var bin = ViewModel?.Items.FirstOrDefault(i => i.IsRecycleBin);
        if (bin is null)
            return false;
        var (pos, size) = ItemMain(bin);
        return cursorMain >= pos && cursorMain <= pos + size;
    }


    // --- Tray icon ---

    private void CreateTrayIcon()
    {
        var menu = new ContextMenu();

        var toggle = new MenuItem { Header = "Show / Hide Dock" };
        toggle.Click += (_, _) => ToggleVisibility();
        menu.Items.Add(toggle);

        var hideTaskbarItem = new MenuItem { Header = "Hide taskbar", IsCheckable = true };
        hideTaskbarItem.Click += (_, _) => SetHideTaskbar(hideTaskbarItem.IsChecked);
        menu.Items.Add(hideTaskbarItem);

        // Theme submenu: Light / Dark / Auto (radio-style checks). "Auto" == DockTheme.System.
        var themeItem = new MenuItem { Header = "Theme" };
        var themeLight = new MenuItem { Header = "Light", IsCheckable = true };
        var themeDark = new MenuItem { Header = "Dark", IsCheckable = true };
        var themeAuto = new MenuItem { Header = "Auto", IsCheckable = true };
        themeLight.Click += (_, _) => SetTheme(DockTheme.Light);
        themeDark.Click += (_, _) => SetTheme(DockTheme.Dark);
        themeAuto.Click += (_, _) => SetTheme(DockTheme.System);
        themeItem.Items.Add(themeLight);
        themeItem.Items.Add(themeDark);
        themeItem.Items.Add(themeAuto);
        menu.Items.Add(themeItem);

        var prefsItem = new MenuItem { Header = "Dock Preferences…" };
        prefsItem.Click += (_, _) => OpenDockPreferences();
        menu.Items.Add(prefsItem);

        // Reflect current settings whenever the menu opens.
        menu.Opened += (_, _) =>
        {
            hideTaskbarItem.IsChecked = ViewModel?.Settings.HideTaskbar ?? false;
            var theme = ViewModel?.Settings.Theme ?? DockTheme.System;
            themeLight.IsChecked = theme == DockTheme.Light;
            themeDark.IsChecked = theme == DockTheme.Dark;
            themeAuto.IsChecked = theme == DockTheme.System;
        };

        menu.Items.Add(new Separator());

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Dockable",
            ContextMenu = menu,
            // The app icon (a URI-backed resource, which H.NotifyIcon accepts) renders the tray glyph.
            IconSource = AppIcon.Tray,
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleVisibility();
        _trayIcon.ForceCreate();
    }

    private void ToggleVisibility()
    {
        if (IsVisible)
        {
            Hide();
            _acrylic.Hide();
        }
        else
        {
            Show();
            PositionDock();
            ApplyGlassEffect();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _appRefreshTimer.Stop();
        _startWatchTimer.Stop();
        _pinCheckTimer.Stop();
        _pinWatcher?.Dispose();
        _minimizeHook.Dispose();
        _thumbnails.Dispose();
        _foreground.Dispose();
        _acrylic.Dispose();
        _appBar?.Unregister(); // release reserved screen space
        Taskbar.Restore(); // restore the taskbar to its pre-launch state
        _trayIcon?.Dispose();
        base.OnClosed(e);
    }
}
