using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace CDArchive.App.Helpers;

/// <summary>
/// Attached behavior that selects (and, unless the user is typing, focuses) the
/// first row of a list the first time it has items — so a freshly-opened
/// single-list view is immediately keyboard-navigable (Home/End/PageUp/PageDown
/// via <see cref="ListKeyboardNavigation"/>) without the user first clicking a
/// row.
///
/// <para>One-shot per list instance: it acts the first time items appear and
/// nothing is selected yet, then stops — so it never fights the user's
/// selection on later sorts/filters/refreshes. Because data loads
/// asynchronously (after <see cref="FrameworkElement.Loaded"/>), it waits on
/// the item collection's change notification when the list starts empty.</para>
///
/// <para>Intended for views with a single dominant list (Albums, Tracks, iTunes
/// Library). Apply explicitly — NOT via an app-wide implicit style — so
/// multi-list editor windows aren't affected.</para>
/// </summary>
public static class AutoSelectFirst
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(AutoSelectFirst),
            new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control list || list is not (ListBox or DataGrid)) return;
        if ((bool)e.NewValue)
            list.Loaded += OnLoaded;
        else
            list.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var list = (Control)sender;
        if (TrySelectFirst(list)) return;   // items already present → done

        // Empty for now (async load). Watch the item collection and act the
        // first time rows appear, then detach.
        if (list is ItemsControl ic && ic.Items is INotifyCollectionChanged incc)
        {
            NotifyCollectionChangedEventHandler? handler = null;
            handler = (_, _) =>
            {
                if (TrySelectFirst(list))
                    incc.CollectionChanged -= handler;
            };
            incc.CollectionChanged += handler;
        }
    }

    /// <summary>
    /// Returns true once the list has items (the signal to stop watching),
    /// selecting the first row when nothing is selected yet.
    /// </summary>
    private static bool TrySelectFirst(Control list)
    {
        var ic = (ItemsControl)list;
        if (ic.Items.Count == 0) return false;

        var selectedIndex = list switch
        {
            ListBox lb  => lb.SelectedIndex,
            DataGrid dg => dg.SelectedIndex,
            _           => 0,
        };

        if (selectedIndex < 0)
        {
            var first = ic.Items[0];
            switch (list)
            {
                case ListBox lb:  lb.SelectedIndex = 0; lb.ScrollIntoView(first); break;
                case DataGrid dg: dg.SelectedIndex = 0; dg.ScrollIntoView(first); break;
            }

            // Give the row keyboard focus so the nav keys work right away.
            // DEFERRED via the dispatcher because at this exact moment the list
            // may be transiently disabled — e.g. the iTunes grid's IsEnabled is
            // bound to IsReady (= !IsBusy) and this runs during the load's
            // collection-reset while IsBusy is still true. You can't focus a
            // disabled element, so an immediate Focus() is silently dropped.
            // By the time the dispatcher runs, the load has finished (IsBusy
            // false → grid enabled) and we re-check focus + materialise the row.
            list.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Don't yank the caret if the user has since started typing.
                if (Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
                if (Keyboard.FocusedElement is ComboBox cb && cb.IsEditable) return;
                if (!list.IsEnabled) return;   // still disabled — give up rather than fight it

                list.UpdateLayout();

                if (list is DataGrid dg)
                {
                    // A DataGrid keeps keyboard focus on a CELL, not the row —
                    // focusing the DataGridRow container is a no-op (unlike a
                    // ListBoxItem). Set the current cell to the first visible
                    // column of row 0, then focus the grid: that routes keyboard
                    // focus into the cell so the nav-key handler (and arrow keys)
                    // fire. This is why the ListBox path worked but the iTunes
                    // grid didn't.
                    if (dg.Columns.Count > 0)
                    {
                        var col = dg.Columns.FirstOrDefault(c => c.Visibility == Visibility.Visible)
                                  ?? dg.Columns[0];
                        dg.CurrentCell = new DataGridCellInfo(dg.Items[0], col);
                    }
                    dg.Focus();
                }
                else if (ic.ItemContainerGenerator.ContainerFromIndex(0) is UIElement container)
                {
                    container.Focus();
                }
            }), DispatcherPriority.Input);
        }
        return true;
    }
}
