using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class AlbumEditorWindow : Window
{
    private readonly CanonPickLists          _pickLists;
    private readonly IReadOnlyList<CanonPiece> _allPieces;
    // PlayerViewModel for the right-click "Play track" / "Play from here"
    // context-menu actions on the track grid. Pre-fix this was pulled via
    // `App.ServiceProvider.GetRequiredService<PlayerViewModel>()` at the
    // point of use — service-locator anti-pattern that coupled the dialog
    // to App's static singleton and made the dialog effectively impossible
    // to unit-test. Plumbed through the ctor now (Rework H12).
    private readonly PlayerViewModel _player;

    // ── Single-edit working state ─────────────────────────────────────────────

    private CanonAlbum          _album;
    // H13 slice 3: Performers and Sessions list state moved onto
    // AlbumEditorViewModel as ObservableCollection<T>. Reference them via
    // _vm.Performers / _vm.Sessions throughout the code-behind.

    // Snapshots captured at load time so we can detect album-level changes to
    // SparsCode / IsStereo / Performers and propagate them down to every track
    // on save. (Tracks always carry their own copy of these fields — there is
    // no "inherit" semantic — so the album editor's job is to push changes to
    // them. Track-level edits via the track editor remain isolated to that track.)
    private AlbumFieldPropagator.InheritableSnapshot _originalInheritable;

    // ── Multi-edit state ──────────────────────────────────────────────────────

    private readonly bool _isMixed;                          // true when editing several albums at once
    private readonly IReadOnlyList<CanonAlbum>? _editAlbums; // the albums being bulk-edited
    // H13 slice 4: the per-window HashSet<string> _mixedFields retired — its job
    // ("which fields started Mixed?") now lives on each MixedField<T> as the
    // StartedMixed property. SaveMulti on the VM consumes that directly.

    // ── Result (single-edit only) ─────────────────────────────────────────────

    public CanonAlbum? Result { get; private set; }

    // ── Mode-driven visibility (H18) ──────────────────────────────────────────
    // Bound from XAML via {Binding Show…, RelativeSource={RelativeSource AncestorType=Window}}.
    // Set in each constructor before the visual tree is rendered; never mutated
    // afterward, so no INotifyPropertyChanged is needed — the binding evaluates
    // once at load. Replaces `MainTabs.Items.Remove(PerformersTab/SessionsTab)`
    // imperative mutation in the multi-edit ctor (didn't survive a re-show —
    // anticipatory; the editor is single-use today).
    public bool ShowPerformersTab { get; private set; } = true;
    public bool ShowSessionsTab   { get; private set; } = true;

    // ── View-model (H13 slice 1: text fields) ────────────────────────────────
    // XAML TwoWay-binds the 7 text fields to _vm.X.Value. The combobox-driven
    // and list-shaped fields stay in code-behind for now — later H13 slices
    // migrate them.
    private readonly AlbumEditorViewModel _vm = new();

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

    public AlbumEditorWindow(CanonPickLists pickLists, IReadOnlyList<CanonPiece> allPieces, PlayerViewModel player, CanonAlbum? album = null)
    {
        InitializeComponent();
        DataContext = _vm;
        _pickLists = pickLists;
        _allPieces = allPieces;
        _player    = player;
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

        // Snapshot the inheritable album-level fields so SaveSingle can detect
        // changes and propagate them down to every track.
        _originalInheritable = AlbumFieldPropagator.Snapshot(_album);

        PopulateDetailsTab();   // _vm.LoadSingle inside also populates Performers + Sessions
        PopulateTrackGrid();
    }

    // ── Constructor: multiple albums (bulk edit) ──────────────────────────────

    public AlbumEditorWindow(CanonPickLists pickLists, IReadOnlyList<CanonAlbum> albums, IReadOnlyList<CanonPiece> allPieces, PlayerViewModel player)
    {
        InitializeComponent();
        DataContext = _vm;
        _pickLists  = pickLists;
        _allPieces  = allPieces;
        _player     = player;
        _isMixed    = true;
        _editAlbums = albums;

        // These aren't used in multi-edit mode but the field must be initialised.
        // Performers / Sessions live on the VM (slice 3) and stay empty in
        // multi-edit since those tabs are hidden (H18).
        _album = new CanonAlbum();

        Title = $"Edit {albums.Count} Albums";

        // Performers / Sessions tabs hidden in multi-edit — Visibility bindings
        // (driven by ShowPerformersTab / ShowSessionsTab) collapse the tab
        // strip entries (H18). Discs & Tracks stays.
        ShowPerformersTab = false;
        ShowSessionsTab   = false;

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

        // Single-edit. H13 slice 1: the 7 text fields load through the VM
        // (TwoWay-bound in XAML). H13 slice 2: SparsCode + IsStereo also
        // load through the VM; the code-behind syncs the non-editable
        // ComboBoxes imperatively below since they use a "Mixed" sentinel
        // ComboBoxItem rather than a placeholder text and the binding
        // story is awkward for dynamically-appended items.
        _vm.LoadSingle(_album);

        SparsCodeCombo.SelectValue(SparsCodeBox, _vm.SparsCode.Value);
        SetStereoComboFromVm();
    }

    private void PopulateMultiDetailsTab()
    {
        var albums = _editAlbums!;

        // H13 slice 1: the 7 text fields' mixed-state model lives on the VM
        // via MixedField<string>. The XAML binding propagates VM.X.Value to
        // TextBox.Text; MixedPlaceholder.Apply below adds the gray-italic
        // chrome + first-edit-clear keystroke wiring on top of the binding.
        _vm.LoadMulti(albums, MixedPlaceholder.PlaceholderText);

        // H13 slice 4: MixedPlaceholder.Apply still owns the gray-italic UI
        // chrome + first-edit-clear keystroke wiring on TextBox / editable
        // ComboBox. The "did this field start Mixed?" tracking that was the
        // _mixedFields HashSet's job now lives on MixedField<T>.StartedMixed,
        // set inside the VM's LoadMulti via InitMixed.
        if (_vm.Title.IsMixed)           MixedPlaceholder.Apply(TitleBox);
        if (_vm.Subtitle.IsMixed)        MixedPlaceholder.Apply(SubtitleBox);
        if (_vm.Label.IsMixed)           MixedPlaceholder.Apply(LabelBox);
        if (_vm.CatalogueNumber.IsMixed) MixedPlaceholder.Apply(CatalogueNumberBox);
        if (_vm.Barcode.IsMixed)         MixedPlaceholder.Apply(BarcodeBox);
        if (_vm.ArchiveFolder.IsMixed)   MixedPlaceholder.Apply(ArchiveFolderBox);
        if (_vm.Notes.IsMixed)           MixedPlaceholder.Apply(NotesBox);

        // H13 slice 2: SparsCode + IsStereo sync from VM. When the VM loaded
        // them as Mixed, append the "Mixed" sentinel ComboBoxItem and select
        // it. The combos' "started Mixed" state is read from
        // _vm.SparsCode.StartedMixed / _vm.IsStereo.StartedMixed at save time.
        if (_vm.SparsCode.IsMixed)
            SparsCodeCombo.AppendMixedSentinel(SparsCodeBox);
        else
            SparsCodeCombo.SelectValue(SparsCodeBox, _vm.SparsCode.Value);

        SetStereoComboFromVm();
    }

    /// <summary>
    /// H13 slice 2: SelectionChanged handler pushes the user's pick back to
    /// the VM. The current value flows to the VM's <see cref="MixedField{T}.Value"/>,
    /// which trips <c>WasEdited = true</c> and (for previously-Mixed cases)
    /// clears <c>IsMixed = false</c>. Save reads from VM.
    /// </summary>
    private void OnSparsCodeChanged(object sender, SelectionChangedEventArgs e)
    {
        var picked = SparsCodeCombo.GetValue(SparsCodeBox);
        // Null/empty from GetValue maps to "Unknown" in the VM's string
        // vocabulary (LoadSingle uses the same normalisation).
        _vm.SparsCode.Value = string.IsNullOrEmpty(picked) ? "Unknown" : picked;
    }

    private void OnStereoChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StereoBox.SelectedItem is not ComboBoxItem cbi) return;
        _vm.IsStereo.Value = cbi.Content as string ?? "Unknown";
    }

    /// <summary>
    /// H13 slice 2: sync <c>StereoBox</c> from <c>_vm.IsStereo.Value</c>. The
    /// dropdown has three fixed items (Unknown / Stereo / Mono) at indexes
    /// 0..2. When the VM is Mixed, we append a "Mixed" sentinel ComboBoxItem
    /// at index 3 (matching the pre-fix pattern) and select it.
    /// </summary>
    private void SetStereoComboFromVm()
    {
        if (_vm.IsStereo.IsMixed)
        {
            StereoBox.Items.Add(new ComboBoxItem
            {
                Content    = AlbumEditorViewModel.IsStereoMixedSentinel,
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic,
            });
            StereoBox.SelectedIndex = 3;
            return;
        }

        StereoBox.SelectedIndex = _vm.IsStereo.Value switch
        {
            "Stereo" => 1,
            "Mono"   => 2,
            _        => 0,   // "Unknown" or anything unexpected
        };
    }

    // H13 slice 1: SetOrMixed / SetOrMixedEditableCombo retired — the
    // text-field mixed-state machinery now lives on AlbumEditorViewModel.
    // The remaining mixed handling for SparsCode + IsStereo (combobox-based)
    // is still in PopulateMultiDetailsTab above and is later H13 territory.

    // ── Performers tab ────────────────────────────────────────────────────────
    // H13 slice 3: list state lives on _vm.Performers (ObservableCollection).
    // XAML's ListView ItemsSource binds to it directly. The Add/Edit/Remove
    // handlers stay in code-behind because they open modal child dialogs
    // needing Window.GetWindow(this) as Owner — legitimate View concern.

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
        _vm.Performers.Add(dlg.Result);
    }

    private void OnEditPerformer(object sender, RoutedEventArgs e)
    {
        if (PerformerList.SelectedItem is not AlbumPerformer selected) return;
        var idx = _vm.Performers.IndexOf(selected);
        var dlg = new PerformerEditorWindow(selected, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _vm.Performers[idx] = dlg.Result;
    }

    private void OnRemovePerformer(object sender, RoutedEventArgs e)
    {
        if (PerformerList.SelectedItem is not AlbumPerformer selected) return;
        _vm.Performers.Remove(selected);
    }

    // ── Sessions tab ─────────────────────────────────────────────────────────
    // Same pattern as Performers above. The OnRemoveSession handler also
    // calls SessionIndexMapping.RemapTracksAfterSessionRemoval (H21) to
    // re-anchor every track's positional SessionIndex before the removal.

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
        _vm.Sessions.Add(dlg.Result);
    }

    private void OnEditSession(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not RecordingSession selected) return;
        var idx = _vm.Sessions.IndexOf(selected);
        var dlg = new SessionEditorWindow(selected) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _vm.Sessions[idx] = dlg.Result;
    }

    private void OnRemoveSession(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not RecordingSession selected) return;

        // H21 (first slice): re-anchor every track's positional
        // SessionIndex BEFORE we mutate the sessions list, so tracks that
        // pointed at the removed session become "no session" and tracks
        // that pointed at later sessions keep addressing the same logical
        // session (index - 1). The pure logic lives in
        // SessionIndexMapping.RemapTracksAfterSessionRemoval so it's
        // unit-tested without WPF. Pre-fix this method just removed the
        // session and every track's SessionIndex silently mis-pointed.
        var removedIndex = _vm.Sessions.IndexOf(selected);
        if (removedIndex >= 0)
        {
            var allTracks = _album.Discs.SelectMany(d => d.Tracks);
            SessionIndexMapping.RemapTracksAfterSessionRemoval(removedIndex, allTracks);
        }

        _vm.Sessions.Remove(selected);
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
    /// In single-edit mode this is the album-level session list held by the
    /// VM (slice 3 — was previously <c>_sessions</c>). In multi-edit mode
    /// each album owns its own session list.
    /// </summary>
    private IList<RecordingSession> SessionsFor(CanonAlbum? album)
    {
        if (!_isMixed) return _vm.Sessions;
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
        // H13 slice 3: no manual refresh needed — TrackEditor's OnAddSession
        // appends to the same ObservableCollection (_vm.Sessions, passed
        // through via SessionsFor) and the ListView ItemsSource binding
        // updates via CollectionChanged automatically.
        PopulateTrackGrid(disc.Tracks.Count > 0 ? disc.Tracks[^1] : null);
    }

    private void OnEditTrack(object sender, RoutedEventArgs e) => OpenTrackEditor();

    // ── Playback context menu ────────────────────────────────────────────────

    private void OnContextPlayTrack(object sender, RoutedEventArgs e) =>
        PlaySelectedTrack(asSingle: true);

    private void OnContextPlayFromHere(object sender, RoutedEventArgs e) =>
        PlaySelectedTrack(asSingle: false);

    private void PlaySelectedTrack(bool asSingle)
    {
        if (TrackList.SelectedItem is not TrackRow row) return;
        // In multi-edit mode the row carries its own album reference; otherwise
        // we're editing the single _album held by this window.
        var album = row.Album ?? _album;

        var result = asSingle
            ? _player.PlaySingleTrack(album, row.Disc, row.Track)
            : _player.PlayFromTrack(album, row.Disc, row.Track);

        if (result == PlayRequestResult.Playing) return;

        var reason = result switch
        {
            PlayRequestResult.NoAudioFile =>
                "No audio file could be located. Check the album's Archive " +
                "Folder field (Details tab), or set this track's FlacPath / " +
                "Mp3Path override.",
            PlayRequestResult.TrackNotInAlbum =>
                "Track is not part of this album. (Save your edits first?)",
            _ => result.ToString(),
        };
        MessageBox.Show(this,
            $"Can't play track {row.Track.TrackNumber}:\n\n{reason}",
            "Playback", MessageBoxButton.OK, MessageBoxImage.Information);
    }

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

            // H13 slice 3: no manual refresh needed — TrackEditor's OnAddSession
        // appends to the same ObservableCollection (_vm.Sessions, passed
        // through via SessionsFor) and the ListView ItemsSource binding
        // updates via CollectionChanged automatically.

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
            IList<RecordingSession>? sharedSessions;
            if (!_isMixed)
            {
                sharedSessions = _vm.Sessions;
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
    //
    // H13 slice 4: data-mutation pass moved into the VM. The code-behind
    // retains only UI-bound bits — validation feedback (MessageBox + tab focus
    // + Title control focus on the missing-Title case) for single-edit, and
    // DialogResult = true for both flows. The VM's SaveSingle / SaveMulti are
    // unit-testable without a WPF host.

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_isMixed)
        {
            _vm.SaveMulti(_editAlbums!);
            DialogResult = true;
            return;
        }

        var error = _vm.SaveSingle(_album, _originalInheritable);
        switch (error)
        {
            case AlbumEditorViewModel.SaveValidationError.MissingTitle:
                MessageBox.Show("Title is required.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                MainTabs.SelectedIndex = 0;
                // When MessageBox.Show returns, WPF restores focus to the OK
                // button (the dialog button that opened the MessageBox) via the
                // dispatcher — AFTER our synchronous call. A direct TitleBox.Focus()
                // here gets clobbered. Defer the focus through the dispatcher at
                // Input priority so it runs after WPF's restoration. Also force a
                // layout pass first because if the Details tab wasn't already
                // active, TitleBox's container has only just been realised.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    TitleBox.UpdateLayout();
                    TitleBox.Focus();
                    Keyboard.Focus(TitleBox);
                    TitleBox.SelectAll();
                }), DispatcherPriority.Input);
                return;

            case AlbumEditorViewModel.SaveValidationError.None:
            default:
                Result = _album;
                DialogResult = true;
                return;
        }
    }
}
