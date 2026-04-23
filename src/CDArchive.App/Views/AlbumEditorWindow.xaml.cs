using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CDArchive.App.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class AlbumEditorWindow : Window
{
    private readonly CanonPickLists          _pickLists;
    private readonly IReadOnlyList<CanonPiece> _allPieces;

    // ── Single-edit working state ─────────────────────────────────────────────

    private CanonAlbum          _album;
    private List<AlbumPerformer> _performers;
    private List<RecordingSession> _sessions;

    // ── Multi-edit state ──────────────────────────────────────────────────────

    private readonly bool _isMixed;                          // true when editing several albums at once
    private readonly IReadOnlyList<CanonAlbum>? _editAlbums; // the albums being bulk-edited
    private readonly HashSet<string> _mixedFields = [];      // field names whose values differ across albums

    // ── Result (single-edit only) ─────────────────────────────────────────────

    public CanonAlbum? Result { get; private set; }

    // ── TrackRow: flat view model for combined disc+track grid ───────────────

    private class TrackRow(AlbumDisc disc, AlbumTrack track, CanonAlbum? album = null)
    {
        public AlbumDisc   Disc       { get; } = disc;
        public AlbumTrack  Track      { get; } = track;
        public CanonAlbum? Album      { get; } = album;
        public int         DiscNumber => Disc.DiscNumber;
        public string?     AlbumTitle => Album?.DisplayTitle;
    }

    // ── Constructor: single album (new or edit) ───────────────────────────────

    public AlbumEditorWindow(CanonPickLists pickLists, IReadOnlyList<CanonPiece> allPieces, CanonAlbum? album = null)
    {
        InitializeComponent();
        _pickLists = pickLists;
        _allPieces = allPieces;
        _isMixed   = false;

        if (album != null)
        {
            Title  = "Edit Album";
            var json = JsonSerializer.Serialize(album);
            _album = JsonSerializer.Deserialize<CanonAlbum>(json)!;
        }
        else
        {
            // Sensible defaults for a new album; pre-populate Disc 1 / Track 1
            _album = new CanonAlbum { IsStereo = true };
            var disc1 = new AlbumDisc { DiscNumber = 1 };
            disc1.Tracks.Add(new AlbumTrack { TrackNumber = 1 });
            _album.Discs.Add(disc1);
        }

        _performers = _album.Performers ?? [];
        _sessions   = _album.Sessions   ?? [];

        PopulateDetailsTab();
        PopulatePerformerList();
        PopulateSessionList();
        PopulateTrackGrid();
    }

    // ── Constructor: multiple albums (bulk edit) ──────────────────────────────

    public AlbumEditorWindow(CanonPickLists pickLists, IReadOnlyList<CanonAlbum> albums, IReadOnlyList<CanonPiece> allPieces)
    {
        InitializeComponent();
        _pickLists  = pickLists;
        _allPieces  = allPieces;
        _isMixed    = true;
        _editAlbums = albums;

        // These aren't used in multi-edit mode but the fields must be initialised
        _album      = new CanonAlbum();
        _performers = [];
        _sessions   = [];

        Title = $"Edit {albums.Count} Albums";

        // Remove Performers and Sessions tabs; Discs & Tracks stays
        MainTabs.Items.Remove(PerformersTab);
        MainTabs.Items.Remove(SessionsTab);

        // Widen the Album column so the user can see which album each track belongs to
        AlbumColumn.Width = 200;
        // Add Disc doesn't make sense across multiple albums
        AddDiscButton.IsEnabled = false;

        PopulateDetailsTab();
        PopulateTrackGrid();
    }

    // ── Details tab ──────────────────────────────────────────────────────────

    private void PopulateDetailsTab()
    {
        LabelBox.ItemsSource = _pickLists.Labels;

        if (_isMixed)
        {
            PopulateMultiDetailsTab();
            return;
        }

        // Single-edit
        TitleBox.Text           = _album.Title           ?? "";
        SubtitleBox.Text        = _album.Subtitle        ?? "";
        LabelBox.Text           = _album.Label           ?? "";
        CatalogueNumberBox.Text = _album.CatalogueNumber ?? "";
        BarcodeBox.Text         = _album.Barcode         ?? "";
        NotesBox.Text           = _album.Notes           ?? "";

        SparsCodeBox.Text = _album.SparsCode ?? "";

        StereoBox.SelectedIndex = _album.IsStereo.HasValue
            ? (_album.IsStereo.Value ? 1 : 2)
            : 0;
    }

    private void PopulateMultiDetailsTab()
    {
        var albums = _editAlbums!;

        SetOrMixed(TitleBox,           "Title",
            albums.Select(a => a.Title           ?? "").Distinct());
        SetOrMixed(SubtitleBox,        "Subtitle",
            albums.Select(a => a.Subtitle        ?? "").Distinct());
        SetOrMixedEditableCombo(LabelBox, "Label",
            albums.Select(a => a.Label           ?? "").Distinct());
        SetOrMixed(CatalogueNumberBox, "CatalogueNumber",
            albums.Select(a => a.CatalogueNumber ?? "").Distinct());
        SetOrMixed(BarcodeBox,         "Barcode",
            albums.Select(a => a.Barcode         ?? "").Distinct());
        SetOrMixedEditableCombo(SparsCodeBox, "SparsCode",
            albums.Select(a => a.SparsCode       ?? "").Distinct());
        SetOrMixed(NotesBox,           "Notes",
            albums.Select(a => a.Notes           ?? "").Distinct());

        // Stereo — non-editable ComboBox; add a "Mixed" sentinel item when needed
        var stereoDistinct = albums.Select(a => a.IsStereo).Distinct().ToList();
        if (stereoDistinct.Count == 1)
        {
            StereoBox.SelectedIndex = stereoDistinct[0] switch { true => 1, false => 2, _ => 0 };
        }
        else
        {
            StereoBox.Items.Add(new ComboBoxItem
            {
                Content   = "Mixed",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            StereoBox.SelectedIndex = 3;   // index of the just-added sentinel
            _mixedFields.Add("IsStereo");
        }
    }

    // ── Mixed-state helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Populates <paramref name="box"/> with the single unanimous value, or shows the
    /// "Mixed" placeholder when the values differ across the selected albums.  See
    /// <see cref="MixedPlaceholder"/> for the clear-on-first-edit behaviour.
    /// </summary>
    private void SetOrMixed(TextBox box, string fieldName, IEnumerable<string> distinctValues)
    {
        var vals = distinctValues.ToList();
        if (vals.Count == 1)
        {
            box.Text = vals[0];
            return;
        }

        _mixedFields.Add(fieldName);
        MixedPlaceholder.Apply(box);
    }

    /// <summary>
    /// Same as <see cref="SetOrMixed"/> but for an <c>IsEditable</c> ComboBox.
    /// </summary>
    private void SetOrMixedEditableCombo(ComboBox box, string fieldName, IEnumerable<string> distinctValues)
    {
        var vals = distinctValues.ToList();
        if (vals.Count == 1)
        {
            box.Text = vals[0];
            return;
        }

        _mixedFields.Add(fieldName);
        MixedPlaceholder.Apply(box);
    }

    // ── Performers tab ────────────────────────────────────────────────────────

    private void PopulatePerformerList()
    {
        PerformerList.ItemsSource = null;
        PerformerList.ItemsSource = _performers;
    }

    private void OnPerformerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = PerformerList.SelectedItem != null;
        EditPerformerButton.IsEnabled   = has;
        RemovePerformerButton.IsEnabled = has;
    }

    private void OnAddPerformer(object sender, RoutedEventArgs e)
    {
        var dlg = new PerformerEditorWindow(null, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _performers.Add(dlg.Result);
        PopulatePerformerList();
    }

    private void OnEditPerformer(object sender, RoutedEventArgs e)
    {
        if (PerformerList.SelectedItem is not AlbumPerformer selected) return;
        var idx = _performers.IndexOf(selected);
        var dlg = new PerformerEditorWindow(selected, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _performers[idx] = dlg.Result;
        PopulatePerformerList();
    }

    private void OnRemovePerformer(object sender, RoutedEventArgs e)
    {
        if (PerformerList.SelectedItem is not AlbumPerformer selected) return;
        _performers.Remove(selected);
        PopulatePerformerList();
    }

    // ── Sessions tab ─────────────────────────────────────────────────────────

    private void PopulateSessionList()
    {
        SessionList.ItemsSource = null;
        SessionList.ItemsSource = _sessions;
    }

    private void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var has = SessionList.SelectedItem != null;
        EditSessionButton.IsEnabled   = has;
        RemoveSessionButton.IsEnabled = has;
    }

    private void OnAddSession(object sender, RoutedEventArgs e)
    {
        var dlg = new SessionEditorWindow(null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _sessions.Add(dlg.Result);
        PopulateSessionList();
    }

    private void OnEditSession(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not RecordingSession selected) return;
        var idx = _sessions.IndexOf(selected);
        var dlg = new SessionEditorWindow(selected) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _sessions[idx] = dlg.Result;
        PopulateSessionList();
    }

    private void OnRemoveSession(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not RecordingSession selected) return;
        _sessions.Remove(selected);
        PopulateSessionList();
    }

    // ── Tab selection ─────────────────────────────────────────────────────────

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard against SelectionChanged events that bubble up from nested selectors
        // (e.g. TrackList, LabelBox). We only want to act on actual tab-navigation events,
        // which always carry a TabItem in AddedItems.
        if (e.AddedItems.Count == 0 || e.AddedItems[0] is not TabItem) return;

        // Auto-select the first row when navigating to the Discs & Tracks tab.
        // Use reference equality so this works regardless of the tab's current index
        // (index 3 in single-edit; index 1 in multi-edit after the other tabs are removed).
        if (!ReferenceEquals(MainTabs.SelectedItem, DiscTracksTab)) return;
        if (TrackList.SelectedIndex < 0 && TrackList.Items.Count > 0)
            TrackList.SelectedIndex = 0;
    }

    // ── Discs & Tracks tab ────────────────────────────────────────────────────

    private void PopulateTrackGrid(AlbumTrack? selectTrack = null)
    {
        List<TrackRow> rows;

        if (_isMixed)
        {
            // Combine tracks from all selected albums; each row carries its source album
            rows = _editAlbums!
                .SelectMany(a => a.Discs
                    .OrderBy(d => d.DiscNumber)
                    .SelectMany(d => d.Tracks.Select(t => new TrackRow(d, t, a))))
                .ToList();
        }
        else
        {
            rows = _album.Discs
                .OrderBy(d => d.DiscNumber)
                .SelectMany(d => d.Tracks.Select(t => new TrackRow(d, t)))
                .ToList();
        }

        TrackList.ItemsSource = null;
        TrackList.ItemsSource = rows;

        if (selectTrack != null)
        {
            var match = rows.FirstOrDefault(r => ReferenceEquals(r.Track, selectTrack));
            if (match != null)
            {
                TrackList.SelectedItem = match;
                TrackList.ScrollIntoView(match);
                return;
            }
        }

        // No specific track to restore — auto-select the first item so the
        // edit/remove buttons remain enabled without requiring a manual click.
        if (rows.Count > 0)
            TrackList.SelectedIndex = 0;
    }

    /// <summary>
    /// Returns the session list to pass to <see cref="TrackEditorWindow"/>.
    /// In single-edit mode this is the album-level session list held in <c>_sessions</c>.
    /// In multi-edit mode each album owns its own session list.
    /// </summary>
    private List<RecordingSession> SessionsFor(CanonAlbum? album)
    {
        if (!_isMixed) return _sessions;
        var a = album ?? _editAlbums![0];
        return a.Sessions ??= [];
    }

    private void OnTrackSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var count = TrackList.SelectedItems.Count;
        // Add Track uses the single-selection anchor, so it requires exactly one row
        AddTrackButton.IsEnabled    = count == 1;
        EditTrackButton.IsEnabled   = count > 0;
        RemoveTrackButton.IsEnabled = count > 0;
    }

    private void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // MouseDoubleClick bubbles — verify the click originated on a row, not on
        // scroll chrome (scrollbar arrows, track, etc.).
        var hit = e.OriginalSource as DependencyObject;
        if (hit == null) return;
        if (hit.FindAncestorOrSelf<ListViewItem>() == null) return;

        if (TrackList.SelectedItems.Count == 0) return;
        e.Handled = true;
        OpenTrackEditor();
    }

    private void OnAddDisc(object sender, RoutedEventArgs e)
    {
        var nextDisc = (_album.Discs.Count > 0 ? _album.Discs.Max(d => d.DiscNumber) : 0) + 1;
        var disc  = new AlbumDisc { DiscNumber = nextDisc };
        var track = new AlbumTrack { TrackNumber = 1 };
        disc.Tracks.Add(track);
        _album.Discs.Add(disc);
        PopulateTrackGrid(track);
    }

    private void OnAddTrack(object sender, RoutedEventArgs e)
    {
        if (TrackList.SelectedItem is not TrackRow selected) return;
        var disc     = selected.Disc;
        var sessions = SessionsFor(selected.Album);
        var dlg = new TrackEditorWindow(disc, disc.Tracks.Count,
                                        sessions, _pickLists, _allPieces) { Owner = this };
        dlg.ShowDialog();
        if (!_isMixed) PopulateSessionList();
        PopulateTrackGrid(disc.Tracks.Count > 0 ? disc.Tracks[^1] : null);
    }

    private void OnEditTrack(object sender, RoutedEventArgs e) => OpenTrackEditor();

    private void OpenTrackEditor()
    {
        var selectedRows = TrackList.SelectedItems.Cast<TrackRow>().ToList();
        if (selectedRows.Count == 0) return;

        if (selectedRows.Count == 1)
        {
            // ── Single track ────────────────────────────────────────────────────
            var row   = selectedRows[0];
            var disc  = row.Disc;
            var index = disc.Tracks.IndexOf(row.Track);
            if (index < 0) return;

            var sessions = SessionsFor(row.Album);
            var dlg = new TrackEditorWindow(disc, index,
                                            sessions, _pickLists, _allPieces) { Owner = this };
            dlg.ShowDialog();

            if (!_isMixed) PopulateSessionList();

            var reselect = index < disc.Tracks.Count ? disc.Tracks[index] : null;
            PopulateTrackGrid(reselect);
        }
        else
        {
            // ── Multiple tracks (bulk edit) ─────────────────────────────────────
            // Every field is shown; values that differ across the selected tracks
            // appear as "Mixed" (gray italic) placeholders. The editor writes back
            // only the fields the user actually changed.
            var tracks = selectedRows.Select(r => r.Track).ToList();

            // If all selected tracks belong to the same album (single-edit mode, or
            // multi-edit mode where the user only picked tracks from one album), pass
            // that album's session list so the Session combo is usable. Otherwise
            // pass null — the Session combo will be disabled in the editor.
            List<RecordingSession>? sharedSessions;
            if (!_isMixed)
            {
                sharedSessions = _sessions;
            }
            else
            {
                var distinctAlbums = selectedRows
                    .Select(r => r.Album)
                    .Where(a => a != null)
                    .Distinct()
                    .ToList();
                sharedSessions = distinctAlbums.Count == 1
                    ? SessionsFor(distinctAlbums[0])
                    : null;
            }

            var dlg = new TrackEditorWindow(tracks, sharedSessions, _pickLists, _allPieces) { Owner = this };
            if (dlg.ShowDialog() != true) return;

            // Rebuild the grid, anchoring on the first edited track, then re-add
            // the rest so the user sees every track that was just edited.
            PopulateTrackGrid(tracks[0]);
            foreach (var track in tracks.Skip(1))
            {
                var match = TrackList.Items.Cast<TrackRow>()
                    .FirstOrDefault(r => ReferenceEquals(r.Track, track));
                if (match != null && !TrackList.SelectedItems.Contains(match))
                    TrackList.SelectedItems.Add(match);
            }
        }
    }

    private void OnRemoveTrack(object sender, RoutedEventArgs e)
    {
        var selectedRows = TrackList.SelectedItems.Cast<TrackRow>().ToList();
        if (selectedRows.Count == 0) return;

        if (selectedRows.Count > 1)
        {
            var confirm = MessageBox.Show(
                $"Remove {selectedRows.Count} tracks?",
                "Remove Tracks",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (confirm != MessageBoxResult.OK) return;
        }

        foreach (var row in selectedRows)
        {
            var disc  = row.Disc;
            disc.Tracks.Remove(row.Track);

            if (disc.Tracks.Count == 0)
            {
                if (_isMixed)
                    row.Album!.Discs.Remove(disc);
                else
                    _album.Discs.Remove(disc);
            }
        }

        PopulateTrackGrid();
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isMixed) { SaveMulti(); return; }

        var title = TitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            MessageBox.Show("Title is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            MainTabs.SelectedIndex = 0;
            TitleBox.Focus();
            return;
        }

        _album.Title           = title;
        _album.Subtitle        = NullIfEmpty(SubtitleBox.Text);
        _album.Label           = NullIfEmpty(LabelBox.Text);
        _album.CatalogueNumber = NullIfEmpty(CatalogueNumberBox.Text);
        _album.Barcode         = NullIfEmpty(BarcodeBox.Text);
        _album.SparsCode       = NullIfEmpty(SparsCodeBox.Text);
        _album.Notes           = NullIfEmpty(NotesBox.Text);
        _album.IsStereo        = StereoBox.SelectedIndex == 1 ? true
                               : StereoBox.SelectedIndex == 2 ? false
                               : (bool?)null;

        _album.Performers = _performers.Count > 0 ? _performers : null;
        _album.Sessions   = _sessions.Count   > 0 ? _sessions   : null;

        _album.Discs.RemoveAll(d => d.Tracks.Count == 0);

        Result = _album;
        DialogResult = true;
    }

    /// <summary>
    /// Applies only the fields that were changed (i.e. not still showing "Mixed")
    /// to every album in the bulk-edit set.
    /// </summary>
    private void SaveMulti()
    {
        // Text / editable-combo fields: skip if the value is still the "Mixed" sentinel
        // or (for mixed fields only) if the user left it empty after clearing the sentinel.
        ApplyText("Title",           TitleBox.Text.Trim(),           v => { foreach (var a in _editAlbums!) a.Title           = v; });
        ApplyText("Subtitle",        SubtitleBox.Text.Trim(),        v => { foreach (var a in _editAlbums!) a.Subtitle        = v; });
        ApplyText("Label",           LabelBox.Text.Trim(),           v => { foreach (var a in _editAlbums!) a.Label           = v; });
        ApplyText("CatalogueNumber", CatalogueNumberBox.Text.Trim(), v => { foreach (var a in _editAlbums!) a.CatalogueNumber = v; });
        ApplyText("Barcode",         BarcodeBox.Text.Trim(),         v => { foreach (var a in _editAlbums!) a.Barcode         = v; });
        ApplyText("SparsCode",       SparsCodeBox.Text.Trim(),       v => { foreach (var a in _editAlbums!) a.SparsCode       = v; });
        ApplyText("Notes",           NotesBox.Text.Trim(),           v => { foreach (var a in _editAlbums!) a.Notes           = v; });

        // Stereo — SelectedIndex 3 is the "Mixed" sentinel; skip if still there
        if (!_mixedFields.Contains("IsStereo") || StereoBox.SelectedIndex != 3)
        {
            var stereo = StereoBox.SelectedIndex == 1 ? (bool?)true
                       : StereoBox.SelectedIndex == 2 ? false
                       : null;
            foreach (var a in _editAlbums!) a.IsStereo = stereo;
        }

        DialogResult = true;
    }

    /// <summary>
    /// Applies <paramref name="newValue"/> to all albums via <paramref name="setter"/>
    /// unless the field was mixed and the user left it as "Mixed" or empty.
    /// </summary>
    private void ApplyText(string fieldName, string newValue, Action<string?> setter)
    {
        if (_mixedFields.Contains(fieldName) &&
            (newValue == "Mixed" || string.IsNullOrEmpty(newValue)))
            return;

        setter(NullIfEmpty(newValue));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
