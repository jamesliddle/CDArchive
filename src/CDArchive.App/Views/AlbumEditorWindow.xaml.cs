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
    // once at load. Multi-edit collapses the Performers / Sessions sections
    // (they're per-album state that doesn't bulk-edit meaningfully).
    public bool ShowPerformersSection { get; private set; } = true;
    public bool ShowSessionsSection   { get; private set; } = true;

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

        // Performers / Sessions sections hidden in multi-edit — Visibility
        // bindings (driven by ShowPerformersSection / ShowSessionsSection)
        // collapse the GroupBoxes (H18 pattern; the field-level VM still
        // initialises empty Performers/Sessions). Discs & Tracks stays.
        ShowPerformersSection = false;
        ShowSessionsSection   = false;

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
        var idx = PerformerList.SelectedIndex;
        var has = idx >= 0;
        EditPerformerButton.IsEnabled   = has;
        RemovePerformerButton.IsEnabled = has;
        PerformerUpButton.IsEnabled     = has && idx > 0;
        PerformerDownButton.IsEnabled   = has && idx < _vm.Performers.Count - 1;
    }

    private void OnPerformerMoveUp(object sender, RoutedEventArgs e)
    {
        var idx = PerformerList.SelectedIndex;
        if (idx <= 0 || idx >= _vm.Performers.Count) return;
        _vm.Performers.Move(idx, idx - 1);
        PerformerList.SelectedIndex = idx - 1;
    }

    private void OnPerformerMoveDown(object sender, RoutedEventArgs e)
    {
        var idx = PerformerList.SelectedIndex;
        if (idx < 0 || idx >= _vm.Performers.Count - 1) return;
        _vm.Performers.Move(idx, idx + 1);
        PerformerList.SelectedIndex = idx + 1;
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

    // ── Session Engineers / Producers lists (mirrors Performers section) ─────

    private void OnSessionEngineerSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateNameListButtons(SessionEngineerList, _vm.SessionEngineers,
            EditSessionEngineerButton, RemoveSessionEngineerButton,
            SessionEngineerUpButton, SessionEngineerDownButton);

    private void OnAddSessionEngineer(object sender, RoutedEventArgs e) =>
        AddNameTo(_vm.SessionEngineers, SessionEngineerList, "Add Engineer", "Engineer name:");

    private void OnEditSessionEngineer(object sender, RoutedEventArgs e) =>
        EditSelectedNameIn(_vm.SessionEngineers, SessionEngineerList, "Edit Engineer", "Engineer name:");

    private void OnRemoveSessionEngineer(object sender, RoutedEventArgs e) =>
        RemoveSelectedNameIn(_vm.SessionEngineers, SessionEngineerList);

    private void OnSessionEngineerMoveUp(object sender, RoutedEventArgs e) =>
        MoveSelectedNameIn(_vm.SessionEngineers, SessionEngineerList, delta: -1);

    private void OnSessionEngineerMoveDown(object sender, RoutedEventArgs e) =>
        MoveSelectedNameIn(_vm.SessionEngineers, SessionEngineerList, delta: +1);

    private void OnSessionEngineerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionEngineerList.SelectedIndex < 0) return;
        OnEditSessionEngineer(sender, e);
    }

    private void OnSessionProducerSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateNameListButtons(SessionProducerList, _vm.SessionProducers,
            EditSessionProducerButton, RemoveSessionProducerButton,
            SessionProducerUpButton, SessionProducerDownButton);

    private void OnAddSessionProducer(object sender, RoutedEventArgs e) =>
        AddNameTo(_vm.SessionProducers, SessionProducerList, "Add Producer", "Producer name:");

    private void OnEditSessionProducer(object sender, RoutedEventArgs e) =>
        EditSelectedNameIn(_vm.SessionProducers, SessionProducerList, "Edit Producer", "Producer name:");

    private void OnRemoveSessionProducer(object sender, RoutedEventArgs e) =>
        RemoveSelectedNameIn(_vm.SessionProducers, SessionProducerList);

    private void OnSessionProducerMoveUp(object sender, RoutedEventArgs e) =>
        MoveSelectedNameIn(_vm.SessionProducers, SessionProducerList, delta: -1);

    private void OnSessionProducerMoveDown(object sender, RoutedEventArgs e) =>
        MoveSelectedNameIn(_vm.SessionProducers, SessionProducerList, delta: +1);

    private void OnSessionProducerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SessionProducerList.SelectedIndex < 0) return;
        OnEditSessionProducer(sender, e);
    }

    // ── Shared single-string list helpers (used by Engineers + Producers) ────

    private void AddNameTo(System.Collections.ObjectModel.ObservableCollection<string> list,
                            ListBox listBox, string title, string prompt)
    {
        var dlg = new NameInputWindow(title, prompt) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list.Add(dlg.Result);
        listBox.SelectedIndex = list.Count - 1;
    }

    private void EditSelectedNameIn(System.Collections.ObjectModel.ObservableCollection<string> list,
                                     ListBox listBox, string title, string prompt)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        var dlg = new NameInputWindow(title, prompt, list[idx]) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list[idx] = dlg.Result;
        listBox.SelectedIndex = idx;
    }

    private static void RemoveSelectedNameIn(System.Collections.ObjectModel.ObservableCollection<string> list,
                                              ListBox listBox)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        list.RemoveAt(idx);
        if (list.Count > 0) listBox.SelectedIndex = Math.Min(idx, list.Count - 1);
    }

    private static void MoveSelectedNameIn(System.Collections.ObjectModel.ObservableCollection<string> list,
                                            ListBox listBox, int delta)
    {
        var idx = listBox.SelectedIndex;
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        list.Move(idx, target);
        listBox.SelectedIndex = target;
    }

    private static void UpdateNameListButtons(
        ListBox listBox, System.Collections.ObjectModel.ObservableCollection<string> list,
        Button editBtn, Button removeBtn, Button upBtn, Button downBtn)
    {
        var idx = listBox.SelectedIndex;
        var has = idx >= 0;
        editBtn.IsEnabled   = has;
        removeBtn.IsEnabled = has;
        upBtn.IsEnabled     = has && idx > 0;
        downBtn.IsEnabled   = has && idx < list.Count - 1;
    }

    // ── Discs & Tracks section ────────────────────────────────────────────────
    // OnTabSelectionChanged retired — the tab strip is gone, so there's no
    // "navigated to Discs & Tracks" event to react to. PopulateTrackGrid below
    // already auto-selects the first row at load time, which is what the old
    // handler did on tab activation.

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
        var disc          = selected.Disc;
        var defaultsAlbum = selected.Album ?? _album;
        var dlg = new TrackEditorWindow(disc, disc.Tracks.Count,
                                        _pickLists, _allPieces, defaultsAlbum) { Owner = this };
        dlg.ShowDialog();
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

            var defaultsAlbum = row.Album ?? _album;
            var dlg = new TrackEditorWindow(disc, index,
                                            _pickLists, _allPieces, defaultsAlbum) { Owner = this };
            dlg.ShowDialog();

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

            // If all selected tracks belong to the same album, we can use that
            // album for the "blank field defaults to album value" semantic in
            // the multi-edit TrackEditor. Otherwise pass null — no defaulting.
            CanonAlbum? defaultsAlbum = null;
            if (!_isMixed)
            {
                defaultsAlbum = _album;
            }
            else
            {
                var distinctAlbums = selectedRows
                    .Select(r => r.Album)
                    .Where(a => a != null)
                    .Distinct()
                    .ToList();
                if (distinctAlbums.Count == 1) defaultsAlbum = distinctAlbums[0];
            }

            var dlg = new TrackEditorWindow(tracks, _pickLists, _allPieces, defaultsAlbum) { Owner = this };
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
                // When MessageBox.Show returns, WPF restores focus to the OK
                // button (the dialog button that opened the MessageBox) via the
                // dispatcher — AFTER our synchronous call. A direct TitleBox.Focus()
                // here gets clobbered. Defer the focus through the dispatcher at
                // Input priority so it runs after WPF's restoration. The
                // single-pane layout means TitleBox is always realised — no
                // tab-switch needed (the pre-fix `MainTabs.SelectedIndex = 0`
                // is gone with the TabControl).
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
