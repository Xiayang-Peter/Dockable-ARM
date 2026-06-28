namespace Dockable.Models;

/// <summary>Which screen edge the dock is anchored to.</summary>
public enum DockEdge
{
    Bottom,
    Left,
    Right,
    Top,
}

/// <summary>Which color theme the dock paints with.</summary>
public enum DockTheme
{
    /// <summary>Follow the current Windows light/dark setting.</summary>
    System,
    Light,
    Dark,
}

/// <summary>How a window animates as it minimizes into / restores from the dock.</summary>
public enum MinimizeEffect
{
    /// <summary>A hard, fast mesh funnel to a point — like paper sucked into a vacuum.</summary>
    Suck,

    /// <summary>Simple scale-down to the dock tile (and reverse on restore).</summary>
    Scale,

    /// <summary>A mesh warp with a bulging neck — like smoke flowing into / out of a bottle.</summary>
    Genie,
}

/// <summary>
/// Root persisted configuration for Dockable. Serialized to
/// %APPDATA%\Dockable\settings.json by <see cref="Services.SettingsStore"/>.
/// </summary>
public sealed class DockSettings
{
    public DockEdge Edge { get; set; } = DockEdge.Bottom;

    /// <summary>Color theme: follow the OS (System) or force Light/Dark.</summary>
    public DockTheme Theme { get; set; } = DockTheme.System;

    /// <summary>Window minimize/restore animation style.</summary>
    public MinimizeEffect MinimizeEffect { get; set; } = MinimizeEffect.Genie;

    /// <summary>Speed multiplier for the minimize/restore effect (1 = default; &gt;1 faster, &lt;1 slower).</summary>
    public double EffectSpeed { get; set; } = 1.0;

    /// <summary>Base (un-magnified) icon size in DIPs.</summary>
    public double IconSize { get; set; } = 48;

    /// <summary>Maximum magnified icon size in DIPs (Phase 2).</summary>
    public double MaxIconSize { get; set; } = 96;

    /// <summary>Cursor influence radius for magnification, in DIPs (Phase 2).</summary>
    public double MagnificationRadius { get; set; } = 160;

    /// <summary>Whether the macOS-style fisheye magnification is enabled (Phase 2).</summary>
    public bool MagnificationEnabled { get; set; } = true;

    /// <summary>Forcefully hide the Windows taskbar while Dockable is running.</summary>
    public bool HideTaskbar { get; set; } = true;

    /// <summary>Show the running-indicator dot under apps that have open windows.</summary>
    public bool ShowRunningIndicators { get; set; } = true;

    /// <summary>Bounce an app's dock icon when it gains a new window (e.g. on launch).</summary>
    public bool AnimateOpeningApps { get; set; } = true;

    /// <summary>
    /// Minimize windows into their app's dock icon (pinned or running) instead of a separate
    /// thumbnail tile. Falls back to a thumbnail tile when the window has no app icon in the dock.
    /// </summary>
    public bool MinimizeIntoIcon { get; set; } = false;

    /// <summary>The user's pinned items, in display order. The Start item is added implicitly.</summary>
    public List<DockItem> Items { get; set; } = new();

    /// <summary>
    /// Dock-owned pinned apps (launch paths: .lnk or .exe), in display order. Null means
    /// "not yet seeded" — on first run it's populated from the current taskbar pin order,
    /// after which the dock owns it (reorder / pin / unpin don't touch the Windows taskbar).
    /// </summary>
    public List<string>? PinnedApps { get; set; }

    public static DockSettings CreateDefault() => new();
}
