using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using Microsoft.Win32;

namespace CDArchive.App.Views;

public partial class TrackEditorWindow : Window
{
    // Disc context — the window operates directly on this disc's Tracks list
    private readonly AlbumDisc? _disc;
    private int _trackIndex;                      // index into _disc.Tracks; >= Count means "adding new"

    // Mutable so OnAddSession can append to the album's session list directly.
    // H13 slice 3: relaxed from List<RecordingSession> to IList<RecordingSession>
    // so AlbumEditorViewModel's ObservableCollection can be passed through
    // directly (it implements IList<T>). The editor's mutations (Add via
    // OnAddSession) propagate back to the AlbumEditor's VM via this shared
    // reference, the same way the pre-fix shared-List propagation worked.
    private readonly IList<RecordingSession>  _sessions;
    private readonly CanonPickLists           _pickLists;
    private readonly IReadOnlyList<CanonPiece> _allPieces;

    // H13 TrackEditor slice 4: PieceRefs + TrackPerformers list state moved
    // onto TrackEditorViewModel as MixedCollection<T>. Reference them via
    // _vm.PieceRefs.Items / _vm.Performers.Items (same ObservableCollection
    // instance the ListView ItemsSource binds to).

    // ── Multi-edit state ──────────────────────────────────────────────────────

    private readonly bool _isMixed;                             // true when editing several tracks at once
    private readonly IReadOnlyList<AlbumTrack>? _editTracks;    // the tracks being bulk-edited
    // True when every track in _editTracks is a loose track (no owning album).
    // Hides TrackNumber + Session UI and gates SaveMulti's write of those fields.
    private readonly bool _allLooseBatch;
    // H13 TrackEditor slice 4: _mixedFields HashSet retired entirely. All
    // fields it previously tracked now carry their own StartedMixed/WasEdited
    // state on the corresponding VM MixedField<T> / MixedCollection<T>.

    // ── Loose-track state ─────────────────────────────────────────────────────
    // True when editing a singleton with no owning album. Hides the track-number /
    // disc / session UI and edits the supplied AlbumTrack in place.
    private readonly bool _isLooseTrack;
    private readonly AlbumTrack? _looseTrack;

    // H13 TrackEditor slice 4: _pieceRefsUntouched / _performersUntouched
    // retired. The VM's MixedCollection<T>.WasEdited carries this state.

    // Position of the SessionBox Mixed-sentinel item (the "Mixed" indicator
    // in multi-edit with mixed values, or the "(multiple albums — cannot edit)"
    // indicator when sessions aren't shared). -1 when no such sentinel is
    // present. Passed into SessionIndexMapping.ResolveSelection so SaveMulti
    // can skip writes when the user left the sentinel selected.
    private int _sessionMixedSentinelIndex = -1;

    // Rework H22 — snapshots taken in the ctor so OnClosing can roll back
    // disc/session mutations when the user clicks Cancel or close-X. JSON
    // deep-clones (matches the AlbumEditorWindow pattern); null means the
    // mode doesn't mutate that container so no snapshot was needed.
    private readonly List<AlbumTrack>?      _tracksSnapshotForRollback;
    private readonly List<RecordingSession>? _sessionsSnapshotForRollback;

    // True while adding new tracks (Next stays enabled, OK adds to disc)
    private bool IsAddingNew => !_isMixed && _disc != null && _trackIndex >= _disc.Tracks.Count;

    // ── View-model (H13 TrackEditor slice 1: text fields) ────────────────────
    // XAML TwoWay-binds the 5 text fields to _vm.X.Value (TrackNumber,
    // Duration, Description, FlacPath, Mp3Path). The combobox-driven, session
    // and list-shaped fields stay in code-behind for now — later slices
    // migrate them.
    private readonly TrackEditorViewModel _vm = new();

    // ── Mode-driven visibility (H18) ──────────────────────────────────────────
    // Bound from XAML via {Binding Show…, RelativeSource={RelativeSource AncestorType=Window}}.
    // Set in each constructor before the visual tree is rendered; never mutated
    // afterward, so no INotifyPropertyChanged is needed — the binding evaluates
    // once at load. Replaces three batches of imperative `Visibility = Collapsed`
    // assignments that didn't survive a re-show (anticipatory; editors are
    // single-use today).
    public bool ShowNavigation   { get; private set; }
    public bool ShowTrackNumber  { get; private set; }
    public bool ShowSession      { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public TrackEditorWindow(
        AlbumDisc disc,
        int trackIndex,
        IList<RecordingSession> sessions,
        CanonPickLists pickLists,
        IReadOnlyList<CanonPiece> allPieces)
    {
        InitializeComponent();
        DataContext = _vm;

        _disc      = disc;
        _trackIndex = trackIndex;
        _sessions  = sessions;
        _pickLists = pickLists;
        _allPieces = allPieces;
        _isMixed   = false;

        ShowNavigation  = true;
        ShowTrackNumber = true;
        ShowSession     = true;

        // Rework H22 — snapshot the disc's track list and the session list
        // before any edits land. Prev/Next commits in single-edit mode mutate
        // _disc.Tracks directly via CommitCurrentTrack, and OnAddSession
        // appends to _sessions directly; without these snapshots, clicking
        // Cancel after navigating away from a track (or adding a new session)
        // would leave the mutations in place. OnClosing rolls back from these
        // snapshots when DialogResult != true.
        _tracksSnapshotForRollback   = DeepClone(disc.Tracks);
        _sessionsSnapshotForRollback = DeepClone(sessions);

        PieceRefList.ItemsSource       = _vm.PieceRefs.Items;
        TrackPerformerList.ItemsSource = _vm.Performers.Items;

        Closing += TrackEditorWindow_Closing;

        LoadTrack();
    }

    // ── Constructor: multiple tracks (bulk edit) ──────────────────────────────

    /// <summary>
    /// Bulk-edit constructor. Pass <paramref name="sessions"/> when all selected tracks
    /// share the same owning album; pass null when the selection spans albums with
    /// different session lists (the Session combo is then disabled).
    /// <para>
    /// <paramref name="allLoose"/> = true when every selected track is a loose
    /// track (no owning album). Hides TrackNumber + Session UI (neither is
    /// meaningful for loose tracks) and SaveMulti skips writing those fields.
    /// Pre-fix the bulk-edit ctor blindly showed TrackNumber for loose batches
    /// and the validation rejected the unanimous "0" sentinel as not-positive.
    /// </para>
    /// </summary>
    public TrackEditorWindow(
        IReadOnlyList<AlbumTrack> tracks,
        IList<RecordingSession>? sessions,
        CanonPickLists pickLists,
        IReadOnlyList<CanonPiece> allPieces,
        bool allLoose = false)
    {
        InitializeComponent();
        DataContext = _vm;

        _disc        = null;
        _trackIndex  = -1;
        _sessions    = sessions ?? [];
        _pickLists   = pickLists;
        _allPieces   = allPieces;
        _isMixed     = true;
        _editTracks  = tracks;
        _allLooseBatch = allLoose;

        ShowNavigation  = false;
        ShowTrackNumber = !allLoose;
        ShowSession     = !allLoose;

        // Rework H22 — snapshot the (possibly caller-owned) session list so
        // OnAddSession appends here can be rolled back on Cancel. Tracks are
        // only written on OK via SaveMulti, so no per-track snapshot is
        // needed in multi-edit mode.
        if (sessions != null)
            _sessionsSnapshotForRollback = DeepClone(sessions);

        PieceRefList.ItemsSource       = _vm.PieceRefs.Items;
        TrackPerformerList.ItemsSource = _vm.Performers.Items;

        Closing += TrackEditorWindow_Closing;

        Title = $"Edit {tracks.Count} Tracks";

        // Prev/Next has no meaning in bulk mode — ShowNavigation drives
        // NavigationPanel.Visibility via XAML binding (H18).

        PopulateMultiFields(sessions != null);
    }

    // ── Constructor: loose track (no owning album) ────────────────────────────

    /// <summary>
    /// Loose-track edit constructor. Singletons have no disc, no sessions, and
    /// no meaningful track number — those UI elements are hidden. Audio-file
    /// overrides stay visible (and effectively required) since there's no
    /// album/disc convention to fall back to for file resolution. The supplied
    /// <paramref name="track"/> is mutated in place on OK.
    /// </summary>
    public TrackEditorWindow(
        AlbumTrack                 track,
        CanonPickLists             pickLists,
        IReadOnlyList<CanonPiece>  allPieces)
    {
        InitializeComponent();
        DataContext = _vm;

        _disc         = null;
        _trackIndex   = -1;
        _sessions     = [];
        _pickLists    = pickLists;
        _allPieces    = allPieces;
        _isMixed      = false;
        _isLooseTrack = true;
        _looseTrack   = track;

        // Hide UI that has no meaning for a loose track. Drives Visibility
        // bindings in XAML (H18) — replaces five imperative .Visibility=Collapsed
        // assignments that didn't survive a re-show.
        ShowNavigation  = false;
        ShowTrackNumber = false;
        ShowSession     = false;

        PieceRefList.ItemsSource       = _vm.PieceRefs.Items;
        TrackPerformerList.ItemsSource = _vm.Performers.Items;

        Title = "Edit Loose Track";

        LoadLooseTrack();
    }

    private void LoadLooseTrack()
    {
        // H13 TrackEditor slice 1: text fields load through the VM
        // (TwoWay-bound in XAML). Slice 2: SparsCode + IsStereo also.
        // Slice 4: PieceRefs + Performers also (via _vm.PieceRefs / Performers
        // — the editor's ListViews bind to those collections directly).
        var t = _looseTrack!;
        _vm.LoadLoose(t);

        SparsCodeCombo.SelectValue(TrackSparsCodeBox, _vm.SparsCode.Value);
        SetStereoComboFromVm();
    }

    // ── Multi-edit: populate every field with unanimous value or "Mixed" ─────

    private void PopulateMultiFields(bool hasSharedSessions)
    {
        var tracks = _editTracks!;

        // ── Scalar text fields (H13 TrackEditor slice 1) ─────────────────────
        // VM owns Mixed/Unanimous state via MixedField<string>. XAML TwoWay-binds
        // TextBoxes to _vm.X.Value; MixedPlaceholder.Apply below adds the gray-
        // italic chrome + first-edit-clear keystroke wiring on top of the
        // binding. The _mixedFields HashSet no longer carries these field names
        // — MixedField<T>.StartedMixed covers the same information per-field.
        // Slice 3: the Session combo also loads through LoadMulti — pass
        // hasSharedSessions so the VM can model the "(multiple albums)"
        // disabled-state via IsMixed=true.
        _vm.LoadMulti(tracks, MixedPlaceholder.PlaceholderText, hasSharedSessions);
        if (_vm.TrackNumber.IsMixed) MixedPlaceholder.Apply(TrackNumberBox);
        if (_vm.Duration.IsMixed)    MixedPlaceholder.Apply(DurationBox);
        if (_vm.Description.IsMixed) MixedPlaceholder.Apply(DescriptionBox);

        // H13 TrackEditor slice 2: SparsCode + IsStereo sync from VM. When
        // the VM loaded them as Mixed, append the "Mixed" sentinel
        // ComboBoxItem and select it. The combos' "started Mixed" state is
        // read from _vm.SparsCode.StartedMixed / _vm.IsStereo.StartedMixed at
        // save time (no _mixedFields entries any more).
        if (_vm.SparsCode.IsMixed)
            SparsCodeCombo.AppendMixedSentinel(TrackSparsCodeBox);
        else
            SparsCodeCombo.SelectValue(TrackSparsCodeBox, _vm.SparsCode.Value);

        SetStereoComboFromVm();

        // ── Audio file overrides ──────────────────────────────────────────────
        // Per-track absolute paths don't bulk-edit meaningfully — disable the
        // whole group when editing multiple tracks at once.
        AudioOverridesGroup.IsEnabled = false;
        AudioOverridesGroup.ToolTip   = "Audio file overrides are per-track and can't be bulk-edited.";

        // ── Session combo ─────────────────────────────────────────────────────
        PopulateMultiSessionCombo(hasSharedSessions);

        // ── Piece References + Performers (H13 TrackEditor slice 4) ──────────
        // VM's LoadMulti above populated the MixedCollection<T>s as Unanimous
        // (shared list across tracks) or Mixed (differing; list empty,
        // StartedMixed=true). The "Mixed" banner visibility tracks the VM's
        // StartedMixed + WasEdited state — show while still untouched, hide
        // on first user Add/Remove.
        if (_vm.PieceRefs.StartedMixed)
        {
            PieceRefsMixedNote.Visibility = Visibility.Visible;
            _vm.PieceRefs.PropertyChanged += OnPieceRefsWasEditedChanged;
        }
        if (_vm.Performers.StartedMixed)
        {
            PerformerMixedNote.Visibility = Visibility.Visible;
            _vm.Performers.PropertyChanged += OnPerformersWasEditedChanged;
        }
    }

    private void OnPieceRefsWasEditedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MixedCollection<TrackPieceRef>.WasEdited) && _vm.PieceRefs.WasEdited)
            PieceRefsMixedNote.Visibility = Visibility.Collapsed;
    }

    private void OnPerformersWasEditedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MixedCollection<AlbumPerformer>.WasEdited) && _vm.Performers.WasEdited)
            PerformerMixedNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// H13 TrackEditor slice 3: drive the session combo's items + selection
    /// from <see cref="TrackEditorViewModel.Session"/> VM state.
    /// <para>
    /// The VM's <c>Session.IsMixed</c> flag distinguishes the two skip-write
    /// cases (single "Mixed" sentinel when selected tracks differ, OR the
    /// "(multiple albums — cannot edit)" disabled-combo state when sessions
    /// aren't shared). <see cref="_sessionMixedSentinelIndex"/> is set to the
    /// item position where that sentinel lives, so <see cref="OnSessionChanged"/>
    /// can skip pushing it to the VM (which would otherwise clear IsMixed).
    /// </para>
    /// </summary>
    private void PopulateMultiSessionCombo(bool hasSharedSessions)
    {
        SessionBox.Items.Clear();

        if (!hasSharedSessions)
        {
            // Selected tracks span albums with different session lists —
            // can't batch-edit. The single item IS the skip-write sentinel
            // (Rework H23). VM is loaded as Mixed; save skips the write.
            SessionLabel.IsEnabled = false;
            SessionBox.IsEnabled   = false;
            SessionBox.Items.Add(new ComboBoxItem
            {
                Content    = "(multiple albums — cannot edit)",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            _sessionMixedSentinelIndex = 0;
            SessionBox.SelectedIndex = 0;
            return;
        }

        foreach (var s in _sessions)
            SessionBox.Items.Add(s.DisplaySummary);

        // "(no session)" pseudo-item — selectable, maps to SessionIndex=null.
        // Pre-fix (Rework H23) this fell back to session 0 via "?? 0".
        SessionBox.Items.Add(NoSessionLabel);

        if (_vm.Session.IsMixed)
        {
            // VM was loaded as Mixed (distinct SessionIndexes across selection).
            // Append the "Mixed" sentinel; OnSessionChanged skips it on save.
            SessionBox.Items.Add(new ComboBoxItem
            {
                Content    = "Mixed",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            _sessionMixedSentinelIndex = SessionBox.Items.Count - 1;
            SessionBox.SelectedIndex   = _sessionMixedSentinelIndex;
        }
        else
        {
            // VM has a unanimous Session.Value (int? — real index or null).
            _sessionMixedSentinelIndex = -1;
            SessionBox.SelectedIndex =
                SessionIndexMapping.InitialComboIndex(_vm.Session.Value, _sessions.Count);
        }
    }

    // H13 TrackEditor slice 4: MarkPerformersTouched retired — the
    // OnPerformersWasEditedChanged handler subscribed in PopulateMultiFields
    // now covers the banner chrome.

    // H13 TrackEditor slice 1: SetOrMixed + SetOrMixedEditableCombo retired —
    // the text-field mixed-state machinery now lives on TrackEditorViewModel.
    // The remaining mixed handling for Session combos and for the PieceRefs /
    // Performers lists is still in PopulateMultiFields above and is later-slice
    // territory.

    // ── Combobox sync (H13 TrackEditor slice 2) ──────────────────────────────

    /// <summary>
    /// SelectionChanged handler for SparsCode. Pushes the user's pick back to
    /// the VM via the translator helper (null/empty maps to "Unknown" in the
    /// VM's string vocabulary, matching LoadSingle's normalisation). Trips
    /// <see cref="MixedField{T}.WasEdited"/> and clears <see cref="MixedField{T}.IsMixed"/>
    /// when the value actually changes (CommunityToolkit's value-equality check
    /// suppresses spurious trips on programmatic syncs).
    /// </summary>
    private void OnSparsCodeChanged(object sender, SelectionChangedEventArgs e)
    {
        var picked = SparsCodeCombo.GetValue(TrackSparsCodeBox);
        _vm.SparsCode.Value = string.IsNullOrEmpty(picked) ? "Unknown" : picked;
    }

    private void OnStereoChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrackStereoBox.SelectedItem is not ComboBoxItem cbi) return;
        _vm.IsStereo.Value = cbi.Content as string ?? "Unknown";
    }

    /// <summary>
    /// Sync <c>TrackStereoBox</c> from <c>_vm.IsStereo.Value</c>. The dropdown
    /// has three fixed items (Unknown / Stereo / Mono) at indexes 0..2. When
    /// the VM is Mixed, append a "Mixed" sentinel ComboBoxItem at index 3 and
    /// select it (matches AlbumEditor's slice-2 pattern).
    /// </summary>
    private void SetStereoComboFromVm()
    {
        if (_vm.IsStereo.IsMixed)
        {
            TrackStereoBox.Items.Add(new ComboBoxItem
            {
                Content    = TrackEditorViewModel.IsStereoMixedSentinel,
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic,
            });
            TrackStereoBox.SelectedIndex = 3;
            return;
        }

        TrackStereoBox.SelectedIndex = _vm.IsStereo.Value switch
        {
            "Stereo" => 1,
            "Mono"   => 2,
            _        => 0,   // "Unknown" or anything unexpected
        };
    }

    // ── Track loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the track at <see cref="_trackIndex"/> into the UI.
    /// If the index is past the end of the list we're in "new track" mode.
    /// </summary>
    private void LoadTrack()
    {
        int? sessionIndexForCombo;

        if (IsAddingNew)
        {
            // New-track mode: defaults only (no source track exists yet).
            _vm.LoadNew(_disc!);
            sessionIndexForCombo = null;
        }
        else
        {
            var track = _disc!.Tracks[_trackIndex];

            // H13 TrackEditor slice 1: text fields load through the VM
            // (TwoWay-bound in XAML). Slice 2: SparsCode + IsStereo also.
            // Slice 4: PieceRefs + Performers also (LoadSingle populates the
            // MixedCollection<T> Items; the ListView ItemsSource binding
            // re-renders automatically).
            _vm.LoadSingle(track);

            sessionIndexForCombo = track.SessionIndex;
        }

        // H13 TrackEditor slice 2: SparsCode + IsStereo also load through
        // the VM; the code-behind syncs the non-editable ComboBoxes
        // imperatively below since they use a "Mixed" sentinel ComboBoxItem
        // rather than a placeholder text and the binding story is awkward
        // for dynamically-appended items.
        SparsCodeCombo.SelectValue(TrackSparsCodeBox, _vm.SparsCode.Value);
        SetStereoComboFromVm();

        // Session combo
        RebuildSessionCombo(sessionIndexForCombo);

        UpdateTitleAndButtons();
    }

    private void UpdateTitleAndButtons()
    {
        if (IsAddingNew)
            Title = $"Add Track (Disc {_disc!.DiscNumber})";
        else
            Title = $"Edit Track {_disc!.Tracks[_trackIndex].TrackNumber}  (Disc {_disc.DiscNumber})";

        PrevButton.IsEnabled = _trackIndex > 0;
        NextButton.IsEnabled = IsAddingNew || _trackIndex < _disc.Tracks.Count - 1;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_trackIndex <= 0) return;
        if (!CommitCurrentTrack()) return;
        _trackIndex--;
        LoadTrack();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (!CommitCurrentTrack()) return;

        // If we just added a new track, _disc.Tracks grew — move to the next slot.
        // If we were editing an existing track, move forward one.
        _trackIndex++;
        LoadTrack();
    }

    // ── Commit ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates, then writes UI state to the disc's track list.
    /// Returns false (and shows a message) if validation fails.
    /// </summary>
    private bool CommitCurrentTrack()
    {
        // H13 TrackEditor slice 1: parse the VM-bound TrackNumber value.
        if (!int.TryParse((_vm.TrackNumber.Value ?? "").Trim(), out var num) || num <= 0)
        {
            MessageBox.Show("Track number must be a positive integer.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TrackNumberBox.Focus();
            return false;
        }

        if (IsAddingNew)
        {
            // Create and append the new track
            var newTrack = new AlbumTrack();
            ApplyUiToTrack(newTrack);
            _disc!.Tracks.Add(newTrack);
        }
        else
        {
            ApplyUiToTrack(_disc!.Tracks[_trackIndex]);
        }

        return true;
    }

    private void ApplyUiToTrack(AlbumTrack target)
    {
        // H13 TrackEditor slice 1: text fields read from the VM (TwoWay-bound,
        // so this is the current TextBox content as the binding propagated it).
        // Slice 2: SparsCode + IsStereo also read from the VM via translator helpers.
        target.TrackNumber  = int.Parse((_vm.TrackNumber.Value ?? "").Trim());
        target.Duration     = NullIfEmpty(_vm.Duration.Value);
        target.SparsCode    = TrackEditorViewModel.SparsCodeFromString(_vm.SparsCode.Value);
        target.IsStereo     = TrackEditorViewModel.IsStereoFromString(_vm.IsStereo.Value);
        target.Description  = NullIfEmpty(_vm.Description.Value);
        target.FlacPath     = NullIfEmpty(_vm.FlacPath.Value);
        target.Mp3Path      = NullIfEmpty(_vm.Mp3Path.Value);
        // H13 TrackEditor slice 4: PieceRefs + Performers read from VM
        // collections. Snapshot to List<T> on save (model field is List<T>?,
        // and storing the VM's ObservableCollection instance directly would
        // be a type mismatch + would tie the model to a UI-facing type).
        target.PieceRefs    = _vm.PieceRefs.Items.Count   > 0 ? _vm.PieceRefs.Items.ToList()   : null;
        // H13 TrackEditor slice 3: SessionIndex reads from VM. Single-edit
        // never has a Mixed sentinel so VM Value is always meaningful.
        target.SessionIndex = _vm.Session.Value;
        target.Performers   = _vm.Performers.Items.Count > 0 ? _vm.Performers.Items.ToList() : null;
    }

    // ── File browse handlers for audio overrides ─────────────────────────────

    private void OnBrowseFlacPath(object sender, RoutedEventArgs e) =>
        BrowseInto(FlacPathBox, "Select FLAC file", "FLAC files (*.flac)|*.flac|All files (*.*)|*.*", "flac");

    private void OnBrowseMp3Path(object sender, RoutedEventArgs e) =>
        BrowseInto(Mp3PathBox, "Select MP3 file", "MP3 files (*.mp3)|*.mp3|All files (*.*)|*.*", "mp3");

    private static void BrowseInto(TextBox target, string title, string filter, string defaultExt)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExt,
            CheckFileExists = true,
        };
        // Pre-seed with the current value's directory if it points somewhere real.
        var current = target.Text.Trim();
        if (!string.IsNullOrEmpty(current))
        {
            var dir = Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                dlg.InitialDirectory = dir;
            if (File.Exists(current))
                dlg.FileName = current;
        }
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FileName;
    }

    // ── Session ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-edit (and OnAddSession) combo rebuild. Items = real sessions +
    /// "(no session)" pseudo-item. No Mixed sentinel in single-edit, so
    /// <see cref="_sessionMixedSentinelIndex"/> is set to -1 to disable the
    /// sentinel-skip path in <see cref="OnSessionChanged"/>.
    /// </summary>
    private void RebuildSessionCombo(int? selectedIndex)
    {
        SessionBox.Items.Clear();
        foreach (var s in _sessions)
            SessionBox.Items.Add(s.DisplaySummary);
        SessionBox.Items.Add(NoSessionLabel);

        _sessionMixedSentinelIndex = -1;
        SessionBox.SelectedIndex =
            SessionIndexMapping.InitialComboIndex(selectedIndex, _sessions.Count);
    }

    /// <summary>
    /// H13 TrackEditor slice 3: SelectionChanged handler for the session combo.
    /// Pushes the user's pick to the VM, EXCEPT when the user selected (or
    /// programmatic code seeded) the skip-write sentinel — that case leaves
    /// the VM as Mixed so SaveMulti skips writing.
    /// </summary>
    private void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = SessionBox.SelectedIndex;
        if (idx < 0) return;

        // Skip the Mixed / "(multiple albums)" sentinel: leave VM as Mixed so
        // save skips. Single-edit has _sessionMixedSentinelIndex = -1 so this
        // check is a no-op there.
        if (_sessionMixedSentinelIndex >= 0 && idx == _sessionMixedSentinelIndex) return;

        // "(no session)" pseudo-item lives at position _sessions.Count.
        if (idx == _sessions.Count)
        {
            _vm.Session.Value = null;
            return;
        }

        // Real session at position idx.
        if (idx < _sessions.Count)
            _vm.Session.Value = idx;
    }

    // The "(no session)" pseudo-item label. Constant so the helper's
    // tests can assert the right item position; the visible text mirrors
    // the convention used elsewhere in the editor for sentinels.
    internal const string NoSessionLabel = "(no session)";

    private void OnAddSession(object sender, RoutedEventArgs e)
    {
        var dlg = new SessionEditorWindow(null) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;

        _sessions.Add(dlg.Result);

        var currentIndex = SessionBox.SelectedIndex >= 0 ? SessionBox.SelectedIndex : (int?)null;
        RebuildSessionCombo(currentIndex);
        SessionBox.SelectedIndex = _sessions.Count - 1;
    }

    // ── OK ────────────────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_isMixed)       { SaveMulti(); return; }
        if (_isLooseTrack)  { CommitLooseTrack(); DialogResult = true; return; }

        if (!CommitCurrentTrack()) return;
        DialogResult = true;
    }

    /// <summary>
    /// Writes the UI state to the supplied loose track in place. No validation
    /// for track number / session — those fields are hidden in loose mode.
    /// </summary>
    private void CommitLooseTrack()
    {
        // H13 TrackEditor slice 1: text fields read from the VM.
        // Slice 2: SparsCode + IsStereo also read from the VM.
        var t = _looseTrack!;
        t.TrackNumber  = 0;             // sentinel: loose track, no disc position
        t.Duration     = NullIfEmpty(_vm.Duration.Value);
        t.SparsCode    = TrackEditorViewModel.SparsCodeFromString(_vm.SparsCode.Value);
        t.IsStereo     = TrackEditorViewModel.IsStereoFromString(_vm.IsStereo.Value);
        t.Description  = NullIfEmpty(_vm.Description.Value);
        t.FlacPath     = NullIfEmpty(_vm.FlacPath.Value);
        t.Mp3Path      = NullIfEmpty(_vm.Mp3Path.Value);
        t.PieceRefs    = _vm.PieceRefs.Items.Count   > 0 ? _vm.PieceRefs.Items.ToList()   : null;
        t.Performers   = _vm.Performers.Items.Count > 0 ? _vm.Performers.Items.ToList() : null;
        t.SessionIndex = null;
    }

    /// <summary>
    /// Applies only the fields that were changed (i.e. not still showing "Mixed")
    /// to every track in the bulk-edit set.
    /// </summary>
    private void SaveMulti()
    {
        // ── Track # ───────────────────────────────────────────────────────────
        // H13 TrackEditor slice 1: read from VM. Skip when:
        //   • allLoose batch (TrackNumber UI hidden; loose tracks stay at 0), or
        //   • field StartedMixed AND user didn't touch (IsMixed still true), or
        //   • field StartedMixed AND user cleared the placeholder without retyping
        //     (Value is empty — don't wipe every track's TrackNumber).
        // Otherwise validate the integer and write.
        if (!_allLooseBatch && !SkipMixedTextWrite(_vm.TrackNumber))
        {
            var trackNumText = (_vm.TrackNumber.Value ?? "").Trim();
            if (!int.TryParse(trackNumText, out var n) || n <= 0)
            {
                MessageBox.Show("Track number must be a positive integer.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                TrackNumberBox.Focus();
                return;
            }
            foreach (var t in _editTracks!) t.TrackNumber = n;
        }

        // ── Simple text fields ────────────────────────────────────────────────
        ApplyMixedFieldText(_vm.Duration,
            v => { foreach (var t in _editTracks!) t.Duration    = v; });
        ApplyMixedFieldText(_vm.Description,
            v => { foreach (var t in _editTracks!) t.Description = v; });

        // ── SPARS Code + Stereo (H13 TrackEditor slice 2) ─────────────────────
        // Combo contract: write IFF !(started Mixed AND still Mixed). The
        // SelectionChanged handlers push every user pick into the VM; if the
        // user never touched a field that loaded Mixed, _vm.X.IsMixed is
        // still true (no SelectionChanged fired) and we skip writing.
        if (!(_vm.SparsCode.StartedMixed && _vm.SparsCode.IsMixed))
        {
            var spars = TrackEditorViewModel.SparsCodeFromString(_vm.SparsCode.Value);
            foreach (var t in _editTracks!) t.SparsCode = spars;
        }

        if (!(_vm.IsStereo.StartedMixed && _vm.IsStereo.IsMixed))
        {
            var stereo = TrackEditorViewModel.IsStereoFromString(_vm.IsStereo.Value);
            foreach (var t in _editTracks!) t.IsStereo = stereo;
        }

        // ── Session (H13 TrackEditor slice 3) ─────────────────────────────────
        // Combo contract: write IFF !(started Mixed AND still Mixed). The
        // OnSessionChanged handler pushes user picks into VM.Session.Value
        // (skipping the sentinel position), so IsMixed clears only when the
        // user actually picked a real session or "(no session)". The
        // "(multiple albums — cannot edit)" case loads as IsMixed=true and the
        // combo is disabled — no user pick possible — so we always skip it.
        if (!(_vm.Session.StartedMixed && _vm.Session.IsMixed))
        {
            foreach (var t in _editTracks!) t.SessionIndex = _vm.Session.Value;
        }

        // ── Piece References + Performers (H13 TrackEditor slice 4) ──────────
        // MixedCollection<T>.ShouldWriteOnSave = !StartedMixed || WasEdited.
        // Unanimous lists always write (idempotent rewrite). Mixed lists write
        // only if the user touched them (Add/Remove); otherwise skip to
        // preserve each track's existing list.
        if (_vm.PieceRefs.ShouldWriteOnSave)
        {
            var refs = _vm.PieceRefs.Items.Count > 0 ? _vm.PieceRefs.Items.ToList() : null;
            foreach (var t in _editTracks!) t.PieceRefs = refs;
        }

        if (_vm.Performers.ShouldWriteOnSave)
        {
            var performers = _vm.Performers.Items.Count > 0 ? _vm.Performers.Items.ToList() : null;
            foreach (var t in _editTracks!) t.Performers = performers;
        }

        DialogResult = true;
    }

    /// <summary>
    /// H13 TrackEditor slice 1: VM-driven equivalent of the old <c>ApplyText</c>.
    /// Same contract as <c>AlbumEditorViewModel.ApplyMixedFieldText</c> — for a
    /// field that started Mixed:
    /// <list type="bullet">
    ///   <item>Still showing the placeholder (<c>field.IsMixed == true</c>) → skip.</item>
    ///   <item>User cleared the placeholder but typed nothing → skip.</item>
    ///   <item>User typed something — apply (via <see cref="NullIfEmpty"/>).</item>
    /// </list>
    /// For fields that started Unanimous, always apply (intentional clears propagate).
    /// </summary>
    private void ApplyMixedFieldText(MixedField<string> field, Action<string?> setter)
    {
        if (SkipMixedTextWrite(field)) return;
        setter(NullIfEmpty(field.Value));
    }

    /// <summary>Returns true when a multi-edit save should skip writing this field.</summary>
    private static bool SkipMixedTextWrite(MixedField<string> field) =>
        field.StartedMixed && (field.IsMixed || string.IsNullOrEmpty(field.Value));

    // ── Piece refs ────────────────────────────────────────────────────────────

    private void OnAddPieceRef(object sender, RoutedEventArgs e)
    {
        var dlg = new PiecePickerWindow(_allPieces) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedRef == null) return;
        _vm.PieceRefs.Items.Add(dlg.SelectedRef);
    }

    private void OnRemovePieceRef(object sender, RoutedEventArgs e)
    {
        if (PieceRefList.SelectedItem is TrackPieceRef selected)
            _vm.PieceRefs.Items.Remove(selected);
    }

    private void OnPieceRefSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = PieceRefList.SelectedItem != null;
        RemovePieceRefButton.IsEnabled = hasSelection;
        EditPieceRefButton.IsEnabled   = hasSelection;
    }

    private void OnPieceRefDoubleClick(object sender, MouseButtonEventArgs e) =>
        EditSelectedPieceRefDetails();

    private void OnEditPieceRefDetails(object sender, RoutedEventArgs e) =>
        EditSelectedPieceRefDetails();

    /// <summary>
    /// Opens the details dialog for the selected ref so the user can attach a
    /// range and/or marker anchors. The ref is mutated in place; we replace
    /// it in <see cref="_vm"/>'s PieceRefs.Items at the same index so the ListBox
    /// re-evaluates <see cref="TrackPieceRef.DisplaySummary"/>.
    /// </summary>
    private void EditSelectedPieceRefDetails()
    {
        if (PieceRefList.SelectedItem is not TrackPieceRef selected) return;
        var idx = _vm.PieceRefs.Items.IndexOf(selected);
        if (idx < 0) return;

        var dlg = new PieceRefDetailsWindow(selected, _allPieces) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.Saved) return;

        // ObservableCollection's indexer raises a Replace event, which is the
        // signal the ListBox needs to re-render the row with the updated
        // DisplaySummary. Same instance — same identity — but the binding
        // refreshes.
        _vm.PieceRefs.Items[idx] = selected;
        PieceRefList.SelectedIndex = idx;
    }

    // ── Track performers ──────────────────────────────────────────────────────

    private void OnAddTrackPerformer(object sender, RoutedEventArgs e)
    {
        var dlg = new PerformerEditorWindow(null, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _vm.Performers.Items.Add(dlg.Result);
    }

    private void OnRemoveTrackPerformer(object sender, RoutedEventArgs e)
    {
        if (TrackPerformerList.SelectedItem is AlbumPerformer selected)
            _vm.Performers.Items.Remove(selected);
    }

    private void OnTrackPerformerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveTrackPerformerButton.IsEnabled = TrackPerformerList.SelectedItem != null;
    }

    // ── Cancel-rollback (Rework H22) ─────────────────────────────────────────

    /// <summary>
    /// JSON deep-clone helper used by the ctors to snapshot the disc track
    /// list and session list before any edits. Matches the pattern used by
    /// <see cref="AlbumEditorWindow"/>. Returns a new <c>List&lt;T&gt;</c>
    /// containing freshly-deserialized copies of every element.
    /// </summary>
    private static List<T> DeepClone<T>(IEnumerable<T> source)
    {
        var json = JsonSerializer.Serialize(source.ToList());
        return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
    }

    /// <summary>
    /// Rolls back per-step mutations on Cancel / close-X. Single-edit's
    /// Prev/Next commits write into <c>_disc.Tracks</c> directly; OnAddSession
    /// appends to <c>_sessions</c> directly. Without this rollback the user's
    /// "Cancel" would be a polite lie. <c>DialogResult == true</c> means the
    /// user clicked OK — leave the changes in place.
    /// </summary>
    private void TrackEditorWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DialogResult == true) return;

        // Restore in dependency order: track list first (tracks reference
        // session positions; the session list snapshot restore will only
        // bring back sessions that existed at editor-open time, but with the
        // session indexes the original tracks were saved with, those stay
        // valid).
        if (_disc != null && _tracksSnapshotForRollback != null)
        {
            _disc.Tracks.Clear();
            foreach (var t in _tracksSnapshotForRollback) _disc.Tracks.Add(t);
        }
        if (_sessionsSnapshotForRollback != null)
        {
            _sessions.Clear();
            foreach (var s in _sessionsSnapshotForRollback) _sessions.Add(s);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
