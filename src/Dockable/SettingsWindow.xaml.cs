using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Dockable.Interop;
using Dockable.Models;
using Dockable.ViewModels;

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
        EffectSpeedSlider.Value = SpeedToSlider(s.EffectSpeed);
        OpenAtLoginSwitch.IsChecked = StartupManager.IsEnabled(StartupEntryName);
        IndicatorsSwitch.IsChecked = s.ShowRunningIndicators;
        AnimateOpeningSwitch.IsChecked = s.AnimateOpeningApps;
        MinimizeIntoIconSwitch.IsChecked = s.MinimizeIntoIcon;
        HideTaskbarSwitch.IsChecked = s.HideTaskbar;

        _initializing = false;
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
    // Slider runs 0..1 with the default at 0.5 = current speed (1x). Mapping is geometric so the
    // midpoint is exactly 1x: position 0 → 0.5x (slow), position 1 → 2x (fast).
    private static double SliderToSpeed(double pos) => Math.Pow(2, (pos - 0.5) * 2);
    private static double SpeedToSlider(double speed) => Math.Clamp(0.5 + Math.Log2(speed) / 2, 0, 1);

    private void EffectSpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing)
            return;
        _vm.Settings.EffectSpeed = SliderToSpeed(e.NewValue);
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
