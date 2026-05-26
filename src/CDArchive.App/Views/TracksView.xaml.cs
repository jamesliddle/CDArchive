using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

/// <summary>
/// Cross-album tracks list. Double-click opens the parent album editor; the
/// Edit button opens <see cref="TrackEditorWindow"/> for the selection
/// (single-track or bulk-edit, mirroring the album editor's track list).
/// </summary>
public partial class TracksView : UserControl
{
    // Maps the base header text (no arrow) to the VM's SortColumn key.
    private static readonly Dictionary<string, string> SortKeys = new()
    {
        ["Album"]    = "AlbumTitle",
        ["Disc"]     = "DiscSort",
        ["Track"]    = "TrackNumber",
        ["Piece"]    = "Piece",
        ["Time"]     = "Duration",
        ["Composer"] = "Composer",
        ["Artist"]   = "PerformerSummary",
    };

    private GridViewColumnHeader? _lastSortHeader;

    public TracksView()
    {
        InitializeComponent();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is TracksViewModel vm)
            await vm.LoadDataCommand.ExecuteAsync(null);
    }

    // ── Toolbar handlers ──────────────────────────────────────────────────────

    private void OnFilterTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is TracksViewModel vm)
            vm.FilterText = FilterBox.Text;
    }

    private void OnShowFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not TracksViewModel vm) return;
        vm.ProvisionalFilter = ShowCombo.SelectedIndex switch
        {
            1 => ProvisionalFilter.Provisional,
            2 => ProvisionalFilter.Accepted,
            _ => ProvisionalFilter.All,
        };
    }

    // H36 (TracksView slice): OnRefreshClick / OnContextApproveTrack /
    // OnContextRejectTrack retired — XAML binds to LoadDataCommand /
    // ApproveTracksCommand / RejectTracksCommand on the VM.
    //
    // Reselect-after-approve dropped intentionally: the AlbumsView slice
    // doesn't reselect either, and a clean RelayCommand round-trip can't
    // easily push state back to the View. If user feedback turns out to
    // miss reselect, a future PR can add an attached behaviour that
    // listens to a "LastApprovedTrackIds" property on the VM.

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        EditButton.IsEnabled = TrackList.SelectedItems.Count > 0;
    }

    // ── Context menu: enable-state only ───────────────────────────────────────

    /// <summary>
    /// Enables Approve only when at least one selected row is still provisional.
    /// Reject is always enabled when there's a selection (the command shows a
    /// confirmation dialog before deleting).
    /// </summary>
    private void OnTrackContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = TrackList.SelectedItems.Cast<AlbumTrackRow>().ToList();
        CtxApproveTrack.IsEnabled = selected.Any(r => r.IsProvisional);
        CtxRejectTrack.IsEnabled  = selected.Count > 0;
    }

    // ── Column-header sort ────────────────────────────────────────────────────

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Column is null) return;             // ignore the filler "padding header"
        if (DataContext is not TracksViewModel vm) return;

        var content  = header.Content?.ToString() ?? "";
        var baseName = content.TrimEnd(' ', '↑', '↓');
        if (!SortKeys.TryGetValue(baseName, out var sortKey)) return;

        // Capture the selection before ApplyFilter replaces vm.Rows (which clears
        // ListView.SelectedItems). We restore it below and use it to anchor the
        // scroll position.
        var selectedBefore = TrackList.SelectedItems.Cast<AlbumTrackRow>().ToList();

        // Clear the arrow on the previously-active header.
        if (_lastSortHeader != null && _lastSortHeader != header)
        {
            var prev = _lastSortHeader.Content?.ToString() ?? "";
            _lastSortHeader.Content = prev.TrimEnd(' ', '↑', '↓');
        }

        if (_lastSortHeader == header)
        {
            vm.SortAscending = !vm.SortAscending;
        }
        else
        {
            vm.SortAscending = true;
            _lastSortHeader  = header;
        }

        header.Content = baseName + (vm.SortAscending ? " ↑" : " ↓");
        vm.SortColumn  = sortKey;
        vm.ApplyFilter();

        // Restore the multi-selection (ApplyFilter built a fresh ObservableCollection),
        // then anchor the scroll on whichever selected row sorts first under the new
        // order — that's the row at the smallest index inside vm.Rows.
        if (selectedBefore.Count == 0) return;

        TrackList.SelectedItems.Clear();
        AlbumTrackRow? anchor = null;
        foreach (var row in TrackList.Items.Cast<AlbumTrackRow>())
        {
            if (!selectedBefore.Any(s => ReferenceEquals(s, row))) continue;
            TrackList.SelectedItems.Add(row);
            anchor ??= row;
        }
        if (anchor != null) ScrollRowToTop(anchor);
    }

    /// <summary>
    /// Positions <paramref name="row"/> at the top of the visible viewport. With
    /// <c>VirtualizingPanel.IsVirtualizing="True"</c> and the default item-unit
    /// scroll mode, the ScrollViewer's vertical offset is measured in item indices,
    /// so scrolling to the row's index puts it at the top (clamped near the end).
    /// </summary>
    private void ScrollRowToTop(AlbumTrackRow row)
    {
        var idx = TrackList.Items.IndexOf(row);
        if (idx < 0) return;

        // Wait one render pass so the ItemsSource swap done by ApplyFilter has
        // settled — the ScrollViewer descendant doesn't exist before the first
        // layout, and item-index scrolling without it is silent no-op.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            TrackList.UpdateLayout();
            var sv = TrackList.FindVisualChild<ScrollViewer>();
            if (sv != null)
                sv.ScrollToVerticalOffset(idx);
            else
                TrackList.ScrollIntoView(row);
        }), DispatcherPriority.Background);
    }

    // ── Double-click → appropriate editor for the row ─────────────────────────

    private async void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // MouseDoubleClick bubbles — bail out unless the click landed on a row.
        var hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;
        if (hit.FindAncestorOrSelf<ListViewItem>() == null) return;

        if (TrackList.SelectedItems.Count != 1) return;
        if (TrackList.SelectedItem is not AlbumTrackRow row) return;
        e.Handled = true;

        if (row.Album is null)
            await EditLooseTrackAsync(row.Track);
        else
            await EditAlbumAsync(row.Album);
    }

    // ── "New Track" toolbar button ────────────────────────────────────────────

    private async void OnNewTrackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TracksViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();
        var fresh = new AlbumTrack { IsProvisional = true };
        var dlg = new TrackEditorWindow(fresh, pickLists, pieces)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        vm.AddLooseTrack(fresh);
        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveLooseTracksAsync();
    }

    // ── Edit button ───────────────────────────────────────────────────────────

    private async void OnEditTracksClick(object sender, RoutedEventArgs e)
    {
        var selected = TrackList.SelectedItems.Cast<AlbumTrackRow>().ToList();
        if (selected.Count == 0) return;

        if (DataContext is not TracksViewModel vm) return;
        var (pieces, pickLists) = await vm.LoadEditorDataAsync();

        var tracks = selected.Select(r => r.Track).ToList();

        // Special-case: a single loose-track selection routes to the loose-mode
        // editor. Mixed or multi-album selections fall through to the bulk-edit
        // constructor (which already supports a flat track list).
        if (selected.Count == 1 && selected[0].Album is null)
        {
            var dlg = new TrackEditorWindow(selected[0].Track, pickLists, pieces)
            {
                Owner = Window.GetWindow(this),
            };
            if (dlg.ShowDialog() != true) return;
        }
        else
        {
            // If every selected row belongs to one album, the track editor can offer
            // its session combo; otherwise pass null so the combo disables itself
            // (this is the same contract AlbumEditorWindow.OpenTrackEditor uses).
            // Loose tracks count as "different albums" for session purposes — they
            // have none.
            var albumOnlyRows  = selected.Where(r => r.Album is not null).ToList();
            var distinctAlbums = albumOnlyRows.Select(r => r.Album!).Distinct().ToList();
            var sharedSessions =
                distinctAlbums.Count == 1 && albumOnlyRows.Count == selected.Count
                    ? distinctAlbums[0].Sessions ?? []
                    : null;

            if (tracks.Count == 1)
            {
                var row  = selected[0];
                var disc = row.Disc!;
                var idx  = disc.Tracks.IndexOf(row.Track);
                if (idx < 0) return;

                var dlg = new TrackEditorWindow(disc, idx, distinctAlbums[0].Sessions ?? [],
                                                pickLists, pieces)
                {
                    Owner = Window.GetWindow(this),
                };
                dlg.ShowDialog();
            }
            else
            {
                // Loose-batch detection: when every selected row is a loose
                // track, hide TrackNumber + Session UI in the bulk editor and
                // skip writing those fields on save (loose tracks have
                // TrackNumber=0 + SessionIndex=null sentinels).
                var allLoose = selected.All(r => r.Album is null);
                var dlg = new TrackEditorWindow(tracks, sharedSessions, pickLists, pieces, allLoose)
                {
                    Owner = Window.GetWindow(this),
                };
                if (dlg.ShowDialog() != true) return;
            }
        }

        // Mutations land on the underlying AlbumTrack instances; refresh the
        // bound collection and persist. SaveAsync writes both albums and the
        // loose-tracks list, so mixed selections are handled in one pass.
        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveAsync();

        // Re-select the rows the user just edited so the highlight survives the
        // ObservableCollection replacement that ApplyFilter performs.
        ReselectTracks(tracks);
    }

    /// <summary>
    /// Opens the loose-mode track editor for an existing loose track. On OK,
    /// the AlbumTrack was mutated in place — we just need to refresh + save.
    /// </summary>
    private async Task EditLooseTrackAsync(AlbumTrack track)
    {
        if (DataContext is not TracksViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();
        var dlg = new TrackEditorWindow(track, pickLists, pieces)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveLooseTracksAsync();
    }

    private void ReselectTracks(IReadOnlyList<AlbumTrack> tracks)
    {
        TrackList.SelectedItems.Clear();
        foreach (var row in TrackList.Items.Cast<AlbumTrackRow>())
        {
            if (tracks.Any(t => ReferenceEquals(t, row.Track)))
                TrackList.SelectedItems.Add(row);
        }
    }

    // ── Album editor opener (used by double-click) ────────────────────────────

    private async Task EditAlbumAsync(CanonAlbum album)
    {
        if (DataContext is not TracksViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();
        var dlg = new AlbumEditorWindow(pickLists, pieces, vm.Player, album)
        {
            Owner = Window.GetWindow(this),
        };

        if (dlg.ShowDialog() != true || dlg.Result is not CanonAlbum result) return;

        // Editor JSON-clones the input on entry and exposes the clone via Result —
        // swap the clone into the shared album list so identity stays coherent
        // (see *Album identity loss across the editor's JSON-clone* in CLAUDE.md).
        vm.ReplaceAlbum(album, result);

        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveAsync();
    }
}
