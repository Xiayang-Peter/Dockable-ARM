using System.Windows.Controls;

namespace Dockable;

/// <summary>Helpers for the code-built context menus (the dock's, the tray's, the menu bar's):
/// the recurring "new MenuItem + Click + Items.Add" triple. Items needing extra properties
/// (dynamic headers, icons, sender-aware handlers) stay hand-built at their call sites.</summary>
internal static class MenuBuilder
{
    /// <summary>Adds a plain item; returns it for callers that need the reference.</summary>
    public static MenuItem AddItem(ItemsControl parent, string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        parent.Items.Add(item);
        return item;
    }

    /// <summary>Adds a checkable item with an initial checked state.</summary>
    public static MenuItem AddCheckable(ItemsControl parent, string header, bool isChecked, Action onClick)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = isChecked };
        item.Click += (_, _) => onClick();
        parent.Items.Add(item);
        return item;
    }
}
