using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace Dockable.Accessibility;

/// <summary>
/// UIA-visible variants of the plain panels used as code-built click targets (fan/grid flyout rows,
/// settings search results, menu-bar pills). A plain StackPanel/Border creates no automation peer,
/// so screen readers can't see or invoke those targets; these subclasses expose a Button control
/// type named by <c>AutomationProperties.Name</c>, with an Invoke pattern that replays the
/// MouseLeftButtonUp the element's existing handlers are wired to.
/// </summary>
public sealed class InvokableRow : StackPanel
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ClickablePeer(this);
}

/// <summary>Border flavour of <see cref="InvokableRow"/> (see its remarks).</summary>
public sealed class InvokableCell : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ClickablePeer(this);
}

/// <summary>Exposes its owner as an invokable UIA Button. Invoke raises a synthesized
/// MouseLeftButtonUp so the owner's existing mouse handlers run unchanged (none of the wired
/// handlers read the event position).</summary>
internal sealed class ClickablePeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    public ClickablePeer(FrameworkElement owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    protected override string GetClassNameCore() => Owner.GetType().Name;

    public override object? GetPattern(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

    public void Invoke() => Owner.Dispatcher.BeginInvoke(() =>
    {
        Owner.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            Source = Owner,
        });
    });
}
