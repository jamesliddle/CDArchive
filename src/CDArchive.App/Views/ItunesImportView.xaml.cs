using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class ItunesImportView : UserControl
{
    /// <summary>
    /// Secondary sort keys for each primary column. Keyed by the column's
    /// <see cref="DataGridColumn.SortMemberPath"/> (which defaults to the bound
    /// property name). Columns absent from this map sort by primary only —
    /// currently Disc, Track, and Time (per user preference).
    /// </summary>
    private static readonly Dictionary<string, string[]> SecondaryKeys = new()
    {
        ["Name"]      = new[] { "Composer",    "Album"                                  },
        ["Genre"]     = new[] { "Composer",    "Album", "DiscNumber", "TrackNumber"     },
        ["Composer"]  = new[] { "Album",       "DiscNumber", "TrackNumber"              },
        ["Album"]     = new[] { "DiscNumber",  "TrackNumber"                            },
        ["Artist"]    = new[] { "Composer",    "Album", "DiscNumber", "TrackNumber"     },
        ["DateAdded"] = new[] { "DiscNumber",  "TrackNumber"                            },
    };

    public ItunesImportView()
    {
        InitializeComponent();
    }

    // H36 (ItunesImportView slice): OnImportSelectedClick retired — XAML now
    // binds `Command="{Binding ImportSelectedTracksCommand}"` with
    // `CommandParameter="{Binding ElementName=TracksGrid, Path=SelectedItems}"`.

    /// <summary>
    /// Replaces the DataGrid's built-in single-column sort with a multi-key
    /// sort that uses the per-column secondaries in <see cref="SecondaryKeys"/>.
    /// Toggles the primary direction the way the default sort would; secondary
    /// keys always sort ascending so the grouping stays stable when the user
    /// flips primary direction. After the sort, scrolls the first selected row
    /// to the top of the viewport (same anchor behaviour as the Tracks list).
    /// </summary>
    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        var grid   = (DataGrid)sender;
        var column = e.Column;
        var primary = column.SortMemberPath;
        if (string.IsNullOrEmpty(primary)) return;

        // Snapshot the current selection. Re-sorting the view below runs
        // through CollectionView.Refresh (via DeferRefresh), which raises a
        // CollectionChanged Reset; WPF's Selector clears SelectedItems on a
        // Reset, so without restoring it the highlighted row(s) would lose
        // their selection on every header click. The items themselves survive
        // (only their order changes), so we re-select the same instances after.
        var selectedSnapshot = grid.SelectedItems.Cast<object>().ToList();
        var primarySelected  = grid.SelectedItem;

        // Toggle direction if the user re-clicks the active column, otherwise
        // start ascending. Mirrors the DataGrid's own behaviour.
        var newDir = column.SortDirection switch
        {
            ListSortDirection.Ascending  => ListSortDirection.Descending,
            ListSortDirection.Descending => ListSortDirection.Ascending,
            _                             => ListSortDirection.Ascending,
        };

        // Clear arrows on every column, set on the clicked one.
        foreach (var c in grid.Columns) c.SortDirection = null;
        column.SortDirection = newDir;

        // Apply the multi-key sort to the live view. Secondary keys always
        // ascend — flipping the primary direction shouldn't shuffle equal-key
        // rows around relative to each other.
        var view = CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view != null)
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(primary, newDir));
                if (SecondaryKeys.TryGetValue(primary, out var secondaries))
                {
                    foreach (var s in secondaries)
                        view.SortDescriptions.Add(new SortDescription(s, ListSortDirection.Ascending));
                }
            }
        }

        // Skip the DataGrid's default single-column sort — we already did the
        // multi-key sort above.
        e.Handled = true;

        if (selectedSnapshot.Count == 0) return;

        // Restore selection + anchor scroll AFTER the grid has finished
        // reacting to the view's Reset. The Reset (raised by the re-sort's
        // DeferRefresh) clears selection AND regenerates the row containers for
        // the new order; that regeneration runs at Render priority. Restoring
        // selection synchronously here — before regeneration — leaves the fresh
        // containers showing unselected. Background priority runs AFTER Render,
        // so by now the containers exist and assigning SelectedItems /
        // SelectedItem actually paints the highlight.
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            grid.SelectedItems.Clear();
            foreach (var item in selectedSnapshot) grid.SelectedItems.Add(item);
            grid.SelectedItem = primarySelected ?? selectedSnapshot[0];

            grid.UpdateLayout();

            // Anchor the scroll on the first selected row's new position.
            var anchor = grid.SelectedItem;
            var idx = grid.Items.IndexOf(anchor);
            if (idx >= 0)
            {
                // DataGrid's row panel runs in virtualizing item-unit mode by
                // default, so vertical offset is measured in item indices.
                var sv = grid.FindVisualChild<ScrollViewer>();
                if (sv != null)
                    sv.ScrollToVerticalOffset(idx);
                else
                    grid.ScrollIntoView(anchor);
            }

            // Belt-and-suspenders: force the selected visual on any realized
            // row containers in case the regenerated container didn't sync its
            // IsSelected from SelectedItems.
            grid.UpdateLayout();
            foreach (var item in selectedSnapshot)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                    row.IsSelected = true;
            }
        }), DispatcherPriority.Background);
    }
}
