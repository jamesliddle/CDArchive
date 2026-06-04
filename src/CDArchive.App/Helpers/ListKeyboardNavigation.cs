using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CDArchive.App.Helpers;

/// <summary>
/// Attached behavior that makes <b>Home / End / PageUp / PageDown</b> navigate a
/// list (<see cref="ListBox"/>, <see cref="ListView"/>, <see cref="DataGrid"/>)
/// whenever keyboard focus is anywhere inside that list — including on a column
/// header (e.g. right after clicking it to re-sort) or the scrollbar, not just
/// on a row.
///
/// <para>WPF already handles these keys natively when a <i>row/item</i> has
/// focus, but not when focus sits on the list's header or another internal
/// part — which is exactly the state left after a header-click sort. Hooking
/// the list's own <see cref="UIElement.PreviewKeyDown"/> (which tunnels through
/// every descendant, headers included) closes that gap. Keys are ignored while
/// a text-editing control has focus so caret movement / typing is unaffected.</para>
///
/// <para>Enable with <c>helpers:ListKeyboardNavigation.Enabled="True"</c> on the
/// list, or app-wide via the implicit ListBox/ListView/DataGrid styles in
/// App.xaml. Each list handles only its own keys, so there's no ambiguity when
/// a view hosts several lists.</para>
/// </summary>
public static class ListKeyboardNavigation
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(ListKeyboardNavigation),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control list) return;
        // Only the supported list controls (ListBox/ListView/DataGrid).
        if (list is not (ListBox or DataGrid)) return;

        if ((bool)e.NewValue)
            list.PreviewKeyDown += OnPreviewKeyDown;
        else
            list.PreviewKeyDown -= OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Home or Key.End or Key.PageUp or Key.PageDown)) return;

        // Don't hijack caret movement / typing in a focused text input.
        if (Keyboard.FocusedElement is TextBoxBase
            || Keyboard.FocusedElement is PasswordBox
            || (Keyboard.FocusedElement is ComboBox cb && cb.IsEditable))
            return;

        var list = (Control)sender;
        var count = ItemCount(list);
        if (count == 0) return;

        var current = SelectedIndex(list);
        var page = Math.Max(1, PageSize(list));

        var target = e.Key switch
        {
            Key.Home     => 0,
            Key.End      => count - 1,
            Key.PageUp   => Math.Max(0, (current < 0 ? 0 : current) - page),
            Key.PageDown => Math.Min(count - 1, (current < 0 ? 0 : current) + page),
            _            => current,
        };
        if (target < 0) target = 0;
        if (target > count - 1) target = count - 1;

        Select(list, target);
        e.Handled = true;
    }

    private static int ItemCount(Control list) => list switch
    {
        ListBox lb  => lb.Items.Count,
        DataGrid dg => dg.Items.Count,
        _           => 0,
    };

    private static int SelectedIndex(Control list) => list switch
    {
        ListBox lb  => lb.SelectedIndex,
        DataGrid dg => dg.SelectedIndex,
        _           => -1,
    };

    private static void Select(Control list, int index)
    {
        object item;
        switch (list)
        {
            case ListBox lb:
                lb.SelectedIndex = index;
                item = lb.Items[index];
                lb.ScrollIntoView(item);
                break;
            case DataGrid dg:
                dg.SelectedIndex = index;
                item = dg.Items[index];
                dg.ScrollIntoView(item);
                break;
            default:
                return;
        }

        // Move keyboard focus onto the new row so subsequent arrow keys (and a
        // visible focus rect) follow it. The container may need a layout pass to
        // materialise after ScrollIntoView under virtualization.
        list.UpdateLayout();
        var generator = (list as ItemsControl)!.ItemContainerGenerator;
        if (generator.ContainerFromIndex(index) is UIElement container)
            container.Focus();
    }

    /// <summary>
    /// Items-per-page for PageUp/PageDown. Uses the inner ScrollViewer's
    /// viewport: in the default item-scrolling mode (<c>CanContentScroll</c>
    /// true) the viewport is measured in items; in pixel-scrolling mode it's
    /// divided by a realized row's height. Falls back to 10 when nothing is
    /// measured yet.
    /// </summary>
    private static int PageSize(Control list)
    {
        var sv = FindDescendant<ScrollViewer>(list);
        if (sv is null) return 10;

        if (sv.CanContentScroll)
        {
            var vp = (int)sv.ViewportHeight;
            return vp > 1 ? vp : 10;
        }

        // Pixel-scrolling: divide pixel viewport by a realized item's height.
        if (list is ItemsControl ic && ic.Items.Count > 0 &&
            ic.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement row &&
            row.ActualHeight > 0)
        {
            var vp = (int)(sv.ViewportHeight / row.ActualHeight);
            return vp > 1 ? vp : 10;
        }
        return 10;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var deeper = FindDescendant<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }
}
