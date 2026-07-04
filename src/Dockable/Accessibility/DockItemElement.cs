using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Dockable.Localization;
using Dockable.ViewModels;

namespace Dockable.Accessibility;

/// <summary>
/// The dock item template's root element. A plain Grid creates no automation peer — the whole dock
/// would be invisible to screen readers — so this subclass exposes each item as a named, invokable
/// UIA element. Hit-testing, the custom mouse drag, and focus (none — the dock is never keyboard
/// focused) are untouched: peers don't participate in input.
/// </summary>
public sealed class DockItemElement : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new DockItemAutomationPeer(this);
}

/// <summary>Presents a dock item to UIA: named from the item's display name, a Button (or
/// Separator) control type, running/minimized state as help text, and Invoke wired to the same
/// dispatch as a mouse click.</summary>
internal sealed class DockItemAutomationPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    public DockItemAutomationPeer(DockItemElement owner) : base(owner) { }

    private DockItemViewModel? Item => (Owner as FrameworkElement)?.DataContext as DockItemViewModel;

    protected override AutomationControlType GetAutomationControlTypeCore()
        => Item?.IsSeparator == true ? AutomationControlType.Separator : AutomationControlType.Button;

    protected override string GetClassNameCore() => "DockItem";

    protected override string GetNameCore() => Item switch
    {
        null => string.Empty,
        { IsStartMenu: true } => Loc.T("A11y_StartMenu"),
        var item => item.DisplayName,
    };

    protected override string GetHelpTextCore() => Item switch
    {
        { IsMinimizedWindow: true } => Loc.T("A11y_MinimizedWindow"),
        { IsRunning: true } => Loc.T("A11y_Running"),
        _ => string.Empty,
    };

    // The children (icon Image, hover label, running dot, badge) would read as nameless noise —
    // the item itself is the whole story, so present it as a leaf.
    protected override List<AutomationPeer> GetChildrenCore() => new();

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Invoke && Item?.IsSeparator != true
            ? this
            : base.GetPattern(patternInterface);

    public void Invoke()
    {
        var owner = (FrameworkElement)Owner;
        owner.Dispatcher.BeginInvoke(() =>
        {
            if (owner.DataContext is DockItemViewModel item && Window.GetWindow(owner) is DockWindow dock)
                dock.ActivateItem(item, owner);
        });
    }
}
