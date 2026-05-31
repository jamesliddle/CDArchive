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
        ["Composer"]   = "Composer",
        ["Performers"] = "PerformerSummary",
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
    /// confirmation dialog before deleting). Edit follows the toolbar Edit
    /// button (any selection); Play Track is single-selection only since the
    /// player loads exactly one file at a time.
    /// </summary>
    private void OnTrackContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = TrackList.SelectedItems.Cast<AlbumTrackRow>().ToList();
        CtxEditTrack.IsEnabled    = selected.Count > 0;
        CtxPlayTrack.IsEnabled    = selected.Count == 1;
        CtxApproveTrack.IsEnabled = selected.Any(r => r.IsProvisional);
        CtxRejectTrack.IsEnabled  = selected.Count > 0;
    }

    // ── Context-menu handlers ─────────────────────────────────────────────────

    private void OnContextEditTrack(object sender, RoutedEventArgs e) =>
        OnEditTracksClick(sender, e);

    private void OnContextPlayTrack(object sender, RoutedEventArgs e)
    {
        if (TrackList.SelectedItem is not AlbumTrackRow row) return;
        if (DataContext is not TracksViewModel vm) return;

        // Loose tracks bypass the locator's convention path (no album/disc to
        // compute folders from) and play directly off their override paths.
        var result = row.Album is null
            ? vm.Player.PlayLooseTrack(row.Track)
            : vm.Player.PlaySingleTrack(row.Album, row.Disc!, row.Track);

        if (result == PlayRequestResult.Playing) return;
        ShowPlaybackError(row, result);
    }

    private static void ShowPlaybackError(AlbumTrackRow row, PlayRequestResult result)
    {
        var reason = result switch
        {
            PlayRequestResult.NoAudioFile when row.Album is null =>
                "No audio file could be located. Loose tracks play from their " +
                "FlacPath / Mp3Path override — set one in the track editor.",
            PlayRequestResult.NoAudioFile =>
                "No audio file could be located. Check the album's Archive " +
                "Folder field, or set this track's FlacPath / Mp3Path override.",
            PlayRequestResult.TrackNotInAlbum =>
                "Track is not part of its album. (Save your edits first?)",
            _ => result.ToString(),
        };
        MessageBox.Show(
            $"Can't play \"{row.Piece}\":\n\n{reason}",
            "Playback", MessageBoxButton.OK, MessageBoxImage.Information);
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

    // ── Double-click → TrackEditor for the row (always — never the album) ─────

    private async void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // MouseDoubleClick bubbles — bail out unless the click landed on a row.
        var hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;
        if (hit.FindAncestorOrSelf<ListViewItem>() == null) return;

        if (TrackList.SelectedItems.Count != 1) return;
        if (TrackList.SelectedItem is not AlbumTrackRow row) return;
        e.Handled = true;

        await EditSingleTrackAsync(row);
    }

    /// <summary>
    /// Opens the TrackEditor for a single row — loose or album-bound, the
    /// only difference is the ctor flavour. Save goes through the atomic
    /// <see cref="TracksViewModel.SaveAsync"/> so a row whose piece-refs got
    /// re-anchored still leaves albums and loose tracks in sync. Used by
    /// the row double-click handler and the single-track branches of
    /// <see cref="OnEditTracksClick"/>.
    /// </summary>
    private async Task EditSingleTrackAsync(AlbumTrackRow row)
    {
        if (DataContext is not TracksViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();

        TrackEditorWindow dlg;
        if (row.Album is null)
        {
            dlg = new TrackEditorWindow(
                disc: null, trackIndex: -1, looseTrack: row.Track,
                pickLists, pieces, defaultsFromAlbum: null);
        }
        else
        {
            var disc = row.Disc;
            if (disc == null) return;
            var idx = disc.Tracks.IndexOf(row.Track);
            if (idx < 0) return;

            dlg = new TrackEditorWindow(
                disc, idx, looseTrack: null,
                pickLists, pieces, defaultsFromAlbum: row.Album);
        }
        dlg.Owner = Window.GetWindow(this);

        if (dlg.ShowDialog() != true) return;

        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveAsync();
        ReselectTracks(new[] { row.Track });
    }

    // ── "New Track" toolbar button ────────────────────────────────────────────

    private async void OnNewTrackClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TracksViewModel vm) return;

        var (pieces, pickLists) = await vm.LoadEditorDataAsync();
        var fresh = new AlbumTrack { IsProvisional = true };
        var dlg = new TrackEditorWindow(
            disc: null, trackIndex: -1, looseTrack: fresh,
            pickLists, pieces, defaultsFromAlbum: null)
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

        // Single selection — share the same path as the row double-click
        // handler. Both flavours (loose / album-bound) flow through one
        // helper.
        if (selected.Count == 1)
        {
            await EditSingleTrackAsync(selected[0]);
            return;
        }

        if (DataContext is not TracksViewModel vm) return;
        var (pieces, pickLists) = await vm.LoadEditorDataAsync();

        // Multi-edit. When every selected row belongs to one album, we can
        // pass that album as the session-default source for blank track
        // fields; mixed selections (multiple albums or any loose) pass null.
        var albumOnlyRows  = selected.Where(r => r.Album is not null).ToList();
        var distinctAlbums = albumOnlyRows.Select(r => r.Album!).Distinct().ToList();
        var defaultsAlbum  =
            distinctAlbums.Count == 1 && albumOnlyRows.Count == selected.Count
                ? distinctAlbums[0]
                : null;

        // Loose-batch detection: when every selected row is a loose track,
        // hide TrackNumber in the bulk editor and skip writing it on save
        // (loose tracks carry the TrackNumber=0 sentinel).
        var allLoose = selected.All(r => r.Album is null);
        var tracks   = selected.Select(r => r.Track).ToList();
        var dlg = new TrackEditorWindow(tracks, pickLists, pieces, defaultsAlbum, allLoose)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() != true) return;

        vm.RebuildRows();
        vm.ApplyFilter();
        await vm.SaveAsync();
        ReselectTracks(tracks);
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

}
