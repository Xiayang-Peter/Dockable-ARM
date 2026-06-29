using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Dockable.Interop;
using Dockable.Localization;
using Dockable.Models;
using Dockable.ViewModels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dockable;

/// <summary>
/// A macOS-style "Dock Preferences" window. Appearance (theme) and the Size/Magnification sliders are
/// wired live; the remaining controls are still visual-only (a later step).
/// </summary>
public partial class SettingsWindow : Window
{
    // Size slider maps directly to IconSize (DIP).
    private const double SizeMin = 12;
    private const double SizeMax = 64;

    // Magnification slider: position 0 = Off, positions 1..MagSteps map MaxIconSize Small..Large.
    private const double MagSmall = 24;
    private const double MagLarge = 104;
    private const int MagSteps = 9; // slider Maximum; step 1 = Small, step 9 = Large

    private static readonly Brush RingBrush = FrozenBrush("#0A84FF");

    private readonly DockViewModel _vm;
    private readonly Action<DockTheme> _setTheme;
    private readonly Action<DockEdge> _setEdge;
    private readonly Action<bool> _setHideTaskbar;
    private readonly Action<GlassEffect> _setGlassEffect;
    private bool _initializing;

    public SettingsWindow(DockViewModel vm, Action<DockTheme> setTheme, Action<DockEdge> setEdge,
        Action<bool> setHideTaskbar, Action<GlassEffect> setGlassEffect)
    {
        _vm = vm;
        _setTheme = setTheme;
        _setEdge = setEdge;
        _setHideTaskbar = setHideTaskbar;
        _setGlassEffect = setGlassEffect;
        _initializing = true; // set before InitializeComponent so initial events no-op
        InitializeComponent();
        Icon = AppIcon.Large;

        var s = vm.Settings;

        // Language: list native names; select the active language by its culture code.
        LanguageCombo.ItemsSource = Loc.Languages.Select(l => l.Name).ToList();
        LanguageCombo.SelectedIndex = Math.Max(0, IndexOfLanguage(Loc.Instance.CurrentCode));

        // Appearance (theme)
        switch (s.Theme)
        {
            case DockTheme.Light: LightRadio.IsChecked = true; break;
            case DockTheme.Dark: DarkRadio.IsChecked = true; break;
            default: AutoRadio.IsChecked = true; break; // System == "Auto"
        }
        UpdateAppearanceRings();

        SizeSlider.Value = Math.Clamp(s.IconSize, SizeMin, SizeMax);
        MagnificationSlider.Value = MagnificationToStep(s);

        PositionCombo.SelectedIndex = (int)s.Edge;   // DockEdge: Bottom, Left, Right, Top (matches combo order)
        GlassEffectCombo.SelectedIndex = (int)s.GlassEffect; // GlassEffect: Simple, Acrylic, LiquidGlass
        MinimizeCombo.SelectedIndex = (int)s.MinimizeEffect; // MinimizeEffect: Suck, Scale, Genie
        EffectSpeedSlider.Value = SpeedToStep(s.EffectSpeed);
        OpenAtLoginSwitch.IsChecked = StartupManager.IsEnabled(StartupEntryName);
        IndicatorsSwitch.IsChecked = s.ShowRunningIndicators;
        AnimateOpeningSwitch.IsChecked = s.AnimateOpeningApps;
        MinimizeIntoIconSwitch.IsChecked = s.MinimizeIntoIcon;
        HideTaskbarSwitch.IsChecked = s.HideTaskbar;

        _initializing = false;

        // Resizable, but never wider/taller than its content nor larger than the screen; it scrolls when
        // the window is shorter than the content. Width is content-driven (no horizontal resize/clip);
        // MaxHeight starts at the viewport so SizeToContent can't overflow it, then is locked to the
        // settled content height in OnPrefsLoaded.
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width);
        MinWidth = MaxWidth = Width;
        MaxHeight = work.Height;
        Loaded += OnPrefsLoaded;
    }

    private void OnPrefsLoaded(object sender, RoutedEventArgs e)
    {
        // The window has sized to its content (capped to the work area). Lock that as the max height,
        // then switch to manual sizing so the user can shrink it (content scrolls) but never grow it
        // past its content or beyond the screen.
        SizeToContent = SizeToContent.Manual;
        MaxHeight = ActualHeight;
    }

    // ResizeMode=CanResize adds a maximize box; remove it so the window can't jump to a corner — its
    // size is already capped to its content and the viewport.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (nint)(uint)(style & ~WINDOW_STYLE.WS_MAXIMIZEBOX));
    }

    // --- Language ---

    private static int IndexOfLanguage(string code)
    {
        var langs = Loc.Languages;
        for (int i = 0; i < langs.Count; i++)
            if (langs[i].Code == code)
                return i;
        return -1;
    }

    // Applies the chosen language live (Loc raises PropertyChanged for bindings + LanguageChanged for
    // the dock's code-built menus) and persists the culture code.
    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        int i = LanguageCombo.SelectedIndex;
        if (i < 0 || i >= Loc.Languages.Count)
            return;
        string code = Loc.Languages[i].Code;
        _vm.Settings.Language = code;
        _vm.Save();
        Loc.Instance.SetLanguage(code);
    }

    // --- Appearance (theme) ---

    // Clicking anywhere on an illustration selects that theme's radio.
    private void AppearanceTile_Click(object sender, MouseButtonEventArgs e)
    {
        switch ((string)((FrameworkElement)sender).Tag)
        {
            case "Light": LightRadio.IsChecked = true; break;
            case "Dark": DarkRadio.IsChecked = true; break;
            default: AutoRadio.IsChecked = true; break;
        }
    }

    private void AppearanceRadio_Checked(object sender, RoutedEventArgs e)
    {
        UpdateAppearanceRings();
        if (_initializing)
            return;
        var theme = (string)((RadioButton)sender).Tag switch
        {
            "Light" => DockTheme.Light,
            "Dark" => DockTheme.Dark,
            _ => DockTheme.System, // "Auto"
        };
        _setTheme(theme); // applies + saves on the dock
    }

    private void UpdateAppearanceRings()
    {
        LightSel.BorderBrush = LightRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
        DarkSel.BorderBrush = DarkRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
        AutoSel.BorderBrush = AutoRadio.IsChecked == true ? RingBrush : Brushes.Transparent;
    }

    // Registry Run-key entry name for launching Dockable itself at login.
    private const string StartupEntryName = "Dockable";

    // Adds/removes Dockable from the Windows startup sequence (HKCU Run key).
    private void OpenAtLoginSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (OpenAtLoginSwitch.IsChecked == true)
        {
            string exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(exe))
                StartupManager.Enable(StartupEntryName, exe);
        }
        else
        {
            StartupManager.Disable(StartupEntryName);
        }
    }

    // Opens the Windows "Startup Apps" settings page in a new window.
    private void StartupApps_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps")
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort; the settings deep-link may be unavailable on some SKUs.
        }
    }

    // Click fires only on user interaction (not the constructor's IsChecked set), so no guard needed.
    private void IndicatorsSwitch_Click(object sender, RoutedEventArgs e)
        => _vm.SetShowRunningIndicators(IndicatorsSwitch.IsChecked == true);

    // On → the taskbar auto-hides while Dockable runs; off → it stays visible. The dock applies and
    // persists it (and restores the taskbar to its pre-launch state on close).
    private void HideTaskbarSwitch_Click(object sender, RoutedEventArgs e)
        => _setHideTaskbar(HideTaskbarSwitch.IsChecked == true);

    private void AnimateOpeningSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.AnimateOpeningApps = AnimateOpeningSwitch.IsChecked == true;
        _vm.Save();
    }

    private void MinimizeIntoIconSwitch_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.MinimizeIntoIcon = MinimizeIntoIconSwitch.IsChecked == true;
        _vm.Save();
    }

    // --- Position on screen ---

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _setEdge((DockEdge)PositionCombo.SelectedIndex); // 0 Bottom, 1 Left, 2 Right, 3 Top
    }

    // --- Glass effect ---

    private void GlassEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _setGlassEffect((GlassEffect)GlassEffectCombo.SelectedIndex); // 0 Simple, 1 Acrylic, 2 LiquidGlass
    }

    // --- Minimize effect ---

    private void MinimizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;
        _vm.Settings.MinimizeEffect = (MinimizeEffect)MinimizeCombo.SelectedIndex; // 0 Suck, 1 Scale, 2 Genie
        _vm.Save();
    }

    // --- Effect speed ---
    // The slider snaps to 5 positions: Slow, ·, Regular, ·, Fast. Speed is the animation's
    // SpeedMultiplier; the scale now tops out at 1x (the base speed) — Slow is very slow (0.01).
    private static readonly double[] SpeedSteps = { 0.01, 0.25, 0.5, 0.75, 1.0 };

    private static double StepToSpeed(int step) => SpeedSteps[Math.Clamp(step, 0, SpeedSteps.Length - 1)];

    private static int SpeedToStep(double speed)
    {
        int best = 0;
        double bestDiff = double.MaxValue;
        for (int i = 0; i < SpeedSteps.Length; i++)
        {
            double diff = Math.Abs(SpeedSteps[i] - speed);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    private void EffectSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _vm.Settings.EffectSpeed = StepToSpeed((int)Math.Round(e.NewValue));
        _vm.Save();
    }

    // --- Size / Magnification ---

    /// <summary>The slider step (0 = Off) that represents the current magnified size.</summary>
    private static double MagnificationToStep(DockSettings s)
    {
        // A magnified size at or below the base size means magnification is effectively off.
        if (!s.MagnificationEnabled || s.MaxIconSize <= s.IconSize)
            return 0;
        double t = (s.MaxIconSize - MagSmall) / (MagLarge - MagSmall); // 0..1 across Small..Large
        int step = (int)Math.Round(t * (MagSteps - 1)) + 1;
        return Math.Clamp(step, 1, MagSteps);
    }

    private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _vm.Settings.IconSize = Math.Round(e.NewValue);
        ApplyAndSave();
    }

    /// <summary>Reflects an externally-changed Size (e.g. a separator drag) onto the slider without
    /// re-applying (the <c>_initializing</c> guard suppresses the resulting ValueChanged).</summary>
    public void SyncSizeFromSettings()
    {
        _initializing = true;
        SizeSlider.Value = Math.Clamp(_vm.Settings.IconSize, SizeMin, SizeMax);
        _initializing = false;
    }

    private void MagnificationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;

        int step = (int)Math.Round(e.NewValue);
        if (step <= 0)
        {
            // "Off" position. (The layout engine also treats MaxIconSize <= IconSize as off.)
            _vm.Settings.MagnificationEnabled = false;
        }
        else
        {
            _vm.Settings.MagnificationEnabled = true;
            _vm.Settings.MaxIconSize = MagSmall + (step - 1) * (MagLarge - MagSmall) / (MagSteps - 1);
        }
        ApplyAndSave();
    }

    private void ApplyAndSave()
    {
        _vm.RecomputeLayout(); // relayout the dock live (resizes + repositions the window)
        _vm.Save();
    }

    private static Brush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
