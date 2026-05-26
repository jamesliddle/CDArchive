using System.Collections.ObjectModel;
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

    private readonly ObservableCollection<TrackPieceRef>  _pieceRefs       = [];
    private readonly ObservableCollection<AlbumPerformer> _trackPerformers = [];

    // ── Multi-edit state ──────────────────────────────────────────────────────

    private readonly bool _isMixed;                             // true when editing several tracks at once
    private readonly IReadOnlyList<AlbumTrack>? _editTracks;    // the tracks being bulk-edited
    private readonly HashSet<string> _mixedFields = [];         // field names whose values differ across tracks

    // ── Loose-track state ─────────────────────────────────────────────────────
    // True when editing a singleton with no owning album. Hides the track-number /
    // disc / session UI and edits the supplied AlbumTrack in place.
    private readonly bool _isLooseTrack;
    private readonly AlbumTrack? _looseTrack;

    // In multi-edit, collection fields start in the "mixed + untouched" state when their
    // values differ across the selected tracks. Any user add/remove/toggle clears this
    // flag, signalling that the new list/state should be applied to every selected track.
    private bool _pieceRefsUntouched;
    private bool _performersUntouched;

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

        PieceRefList.ItemsSource       = _pieceRefs;
        TrackPerformerList.ItemsSource = _trackPerformers;

        Closing += TrackEditorWindow_Closing;

        LoadTrack();
    }

    // ── Constructor: multiple tracks (bulk edit) ──────────────────────────────

    /// <summary>
    /// Bulk-edit constructor. Pass <paramref name="sessions"/> when all selected tracks
    /// share the same owning album; pass null when the selection spans albums with
    /// different session lists (the Session combo is then disabled).
    /// </summary>
    public TrackEditorWindow(
        IReadOnlyList<AlbumTrack> tracks,
        IList<RecordingSession>? sessions,
        CanonPickLists pickLists,
        IReadOnlyList<CanonPiece> allPieces)
    {
        InitializeComponent();
        DataContext = _vm;

        _disc       = null;
        _trackIndex = -1;
        _sessions   = sessions ?? [];
        _pickLists  = pickLists;
        _allPieces  = allPieces;
        _isMixed    = true;
        _editTracks = tracks;

        ShowNavigation  = false;
        ShowTrackNumber = true;
        ShowSession     = true;

        // Rework H22 — snapshot the (possibly caller-owned) session list so
        // OnAddSession appends here can be rolled back on Cancel. Tracks are
        // only written on OK via SaveMulti, so no per-track snapshot is
        // needed in multi-edit mode.
        if (sessions != null)
            _sessionsSnapshotForRollback = DeepClone(sessions);

        PieceRefList.ItemsSource       = _pieceRefs;
        TrackPerformerList.ItemsSource = _trackPerformers;

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

        PieceRefList.ItemsSource       = _pieceRefs;
        TrackPerformerList.ItemsSource = _trackPerformers;

        Title = "Edit Loose Track";

        LoadLooseTrack();
    }

    private void LoadLooseTrack()
    {
        var t = _looseTrack!;
        _pieceRefs.Clear();
        foreach (var r in t.PieceRefs ?? []) _pieceRefs.Add(r);
        _trackPerformers.Clear();
        foreach (var p in t.Performers ?? []) _trackPerformers.Add(p);

        // H13 TrackEditor slice 1: text fields load through the VM
        // (TwoWay-bound in XAML).
        _vm.LoadLoose(t);

        SparsCodeCombo.SelectValue(TrackSparsCodeBox, t.SparsCode);
        TrackStereoBox.SelectedIndex = t.IsStereo switch { true => 1, false => 2, _ => 0 };
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
        _vm.LoadMulti(tracks, MixedPlaceholder.PlaceholderText);
        if (_vm.TrackNumber.IsMixed) MixedPlaceholder.Apply(TrackNumberBox);
        if (_vm.Duration.IsMixed)    MixedPlaceholder.Apply(DurationBox);
        if (_vm.Description.IsMixed) MixedPlaceholder.Apply(DescriptionBox);

        if (SparsCodeCombo.PopulateMixed(TrackSparsCodeBox, tracks.Select(t => t.SparsCode)))
            _mixedFields.Add("SparsCode");

        // Stereo — non-editable ComboBox; add a "Mixed" sentinel item when needed.
        // SelectedIndex: 0=Unknown (null), 1=Stereo (true), 2=Mono (false), 3=Mixed sentinel.
        var stereoDistinct = tracks.Select(t => t.IsStereo).Distinct().ToList();
        if (stereoDistinct.Count == 1)
        {
            TrackStereoBox.SelectedIndex = stereoDistinct[0] switch { true => 1, false => 2, _ => 0 };
        }
        else
        {
            TrackStereoBox.Items.Add(new ComboBoxItem
            {
                Content    = "Mixed",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            TrackStereoBox.SelectedIndex = 3;
            _mixedFields.Add("IsStereo");
        }

        // ── Audio file overrides ──────────────────────────────────────────────
        // Per-track absolute paths don't bulk-edit meaningfully — disable the
        // whole group when editing multiple tracks at once.
        AudioOverridesGroup.IsEnabled = false;
        AudioOverridesGroup.ToolTip   = "Audio file overrides are per-track and can't be bulk-edited.";

        // ── Session combo ─────────────────────────────────────────────────────
        PopulateMultiSessionCombo(hasSharedSessions);

        // ── Piece References ──────────────────────────────────────────────────
        var pieceRefFingerprints = tracks
            .Select(t => JsonSerializer.Serialize(t.PieceRefs ?? []))
            .Distinct()
            .ToList();

        if (pieceRefFingerprints.Count == 1)
        {
            foreach (var r in tracks[0].PieceRefs ?? [])
                _pieceRefs.Add(r);
        }
        else
        {
            // Mixed — leave list empty; first Add/Remove replaces the list for every track
            PieceRefsMixedNote.Visibility = Visibility.Visible;
            _mixedFields.Add("PieceRefs");
            _pieceRefsUntouched = true;
            _pieceRefs.CollectionChanged += (_, _) =>
            {
                _pieceRefsUntouched = false;
                PieceRefsMixedNote.Visibility = Visibility.Collapsed;
            };
        }

        // ── Performers ────────────────────────────────────────────────────────
        // Track performers are always shown (no override checkbox). When all
        // selected tracks share the same list, load it; when they differ, show
        // a "Mixed" banner that the first add/remove clears.
        var performerFingerprints = tracks
            .Select(t => JsonSerializer.Serialize(t.Performers ?? []))
            .Distinct()
            .ToList();

        if (performerFingerprints.Count == 1)
        {
            foreach (var p in tracks[0].Performers ?? [])
                _trackPerformers.Add(p);
        }
        else
        {
            PerformerMixedNote.Visibility = Visibility.Visible;
            _mixedFields.Add("Performers");
            _performersUntouched = true;
            _trackPerformers.CollectionChanged += (_, _) => MarkPerformersTouched();
        }
    }

    private void PopulateMultiSessionCombo(bool hasSharedSessions)
    {
        SessionBox.Items.Clear();

        if (!hasSharedSessions)
        {
            // Selected tracks span albums with different session lists —
            // can't batch-edit. The single item IS the skip-write sentinel
            // (Rework H23): treat it like the "Mixed" sentinel so SaveMulti
            // leaves each track's existing SessionIndex alone.
            SessionLabel.IsEnabled = false;
            SessionBox.IsEnabled   = false;
            SessionBox.Items.Add(new ComboBoxItem
            {
                Content    = "(multiple albums — cannot edit)",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            SessionBox.SelectedIndex = 0;
            _mixedFields.Add("SessionIndex");
            _sessionMixedSentinelIndex = 0;
            return;
        }

        foreach (var s in _sessions)
            SessionBox.Items.Add(s.DisplaySummary);

        // "(no session)" pseudo-item, same as the single-edit combo. Without
        // it the "all selected tracks have SessionIndex=null" branch below
        // pre-fix collapsed to session 0 via "?? 0" — silent data
        // corruption on Save (Rework H23).
        SessionBox.Items.Add(NoSessionLabel);

        var distinctIndexes = _editTracks!
            .Select(t => t.SessionIndex)
            .Distinct()
            .ToList();

        if (distinctIndexes.Count == 1)
        {
            // Uniform selection — initialise the combo to the shared value
            // (real session, or "(no session)" when null).
            SessionBox.SelectedIndex =
                SessionIndexMapping.InitialComboIndex(distinctIndexes[0], _sessions.Count);
        }
        else
        {
            // Append a "Mixed" sentinel at the end; SaveMulti's commit logic
            // skips the write when this is still selected.
            SessionBox.Items.Add(new ComboBoxItem
            {
                Content    = "Mixed",
                Foreground = Brushes.DarkGray,
                FontStyle  = FontStyles.Italic
            });
            SessionBox.SelectedIndex = SessionBox.Items.Count - 1;
            _mixedFields.Add("SessionIndex");
            _sessionMixedSentinelIndex = SessionBox.Items.Count - 1;
        }
    }

    private void MarkPerformersTouched()
    {
        _performersUntouched = false;
        PerformerMixedNote.Visibility = Visibility.Collapsed;
    }

    // H13 TrackEditor slice 1: SetOrMixed + SetOrMixedEditableCombo retired —
    // the text-field mixed-state machinery now lives on TrackEditorViewModel.
    // The remaining mixed handling for SparsCode / IsStereo / Session combos and
    // for the PieceRefs / Performers lists is still in PopulateMultiFields above
    // and is later-slice territory.

    // ── Track loading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the track at <see cref="_trackIndex"/> into the UI.
    /// If the index is past the end of the list we're in "new track" mode.
    /// </summary>
    private void LoadTrack()
    {
        // Copy collections so the UI works on independent data
        _pieceRefs.Clear();
        _trackPerformers.Clear();

        int? sessionIndexForCombo;
        string? sparsCodeForCombo;
        bool? isStereoForCombo;

        if (IsAddingNew)
        {
            // New-track mode: defaults only (no source track exists yet).
            _vm.LoadNew(_disc!);
            sessionIndexForCombo = null;
            sparsCodeForCombo    = null;
            isStereoForCombo     = null;
        }
        else
        {
            var track = _disc!.Tracks[_trackIndex];
            foreach (var r in track.PieceRefs ?? []) _pieceRefs.Add(r);
            foreach (var p in track.Performers ?? []) _trackPerformers.Add(p);

            // H13 TrackEditor slice 1: text fields load through the VM
            // (TwoWay-bound in XAML).
            _vm.LoadSingle(track);

            sessionIndexForCombo = track.SessionIndex;
            sparsCodeForCombo    = track.SparsCode;
            isStereoForCombo     = track.IsStereo;
        }

        // Combobox-driven fields stay in code-behind until slice 2.
        SparsCodeCombo.SelectValue(TrackSparsCodeBox, sparsCodeForCombo);
        TrackStereoBox.SelectedIndex = isStereoForCombo switch { true => 1, false => 2, _ => 0 };

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
        target.TrackNumber  = int.Parse((_vm.TrackNumber.Value ?? "").Trim());
        target.Duration     = NullIfEmpty(_vm.Duration.Value);
        target.SparsCode    = SparsCodeCombo.GetValue(TrackSparsCodeBox);
        target.IsStereo     = TrackStereoBox.SelectedIndex == 1 ? true
                            : TrackStereoBox.SelectedIndex == 2 ? false
                            : (bool?)null;
        target.Description  = NullIfEmpty(_vm.Description.Value);
        target.FlacPath     = NullIfEmpty(_vm.FlacPath.Value);
        target.Mp3Path      = NullIfEmpty(_vm.Mp3Path.Value);
        target.PieceRefs    = _pieceRefs.Count > 0 ? [.. _pieceRefs] : null;
        // SessionIndexMapping treats anything past the real session list
        // (including the "(no session)" pseudo-item and any negative
        // SelectedIndex) as null — no silent collapse to session 0.
        // Single-edit never has a Mixed sentinel, so pass -1.
        target.SessionIndex = SessionIndexMapping.ResolveSelection(
            SessionBox.SelectedIndex, _sessions.Count,
            mixedSentinelIndex: -1, out _);
        target.Performers   = _trackPerformers.Count > 0 ? [.. _trackPerformers] : null;
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

    private void RebuildSessionCombo(int? selectedIndex)
    {
        SessionBox.Items.Clear();
        foreach (var s in _sessions)
            SessionBox.Items.Add(s.DisplaySummary);

        // "(no session)" pseudo-item — selectable, maps to SessionIndex=null.
        // Pre-fix (Rework H23) this fell back to session 0 via "?? 0", so
        // opening a no-session track and clicking OK silently wrote
        // SessionIndex = 0. See SessionIndexMapping for the contract.
        SessionBox.Items.Add(NoSessionLabel);

        SessionBox.SelectedIndex =
            SessionIndexMapping.InitialComboIndex(selectedIndex, _sessions.Count);
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
        var t = _looseTrack!;
        t.TrackNumber  = 0;             // sentinel: loose track, no disc position
        t.Duration     = NullIfEmpty(_vm.Duration.Value);
        t.SparsCode    = SparsCodeCombo.GetValue(TrackSparsCodeBox);
        t.IsStereo     = TrackStereoBox.SelectedIndex == 1 ? true
                       : TrackStereoBox.SelectedIndex == 2 ? false
                       : (bool?)null;
        t.Description  = NullIfEmpty(_vm.Description.Value);
        t.FlacPath     = NullIfEmpty(_vm.FlacPath.Value);
        t.Mp3Path      = NullIfEmpty(_vm.Mp3Path.Value);
        t.PieceRefs    = _pieceRefs.Count > 0 ? [.. _pieceRefs] : null;
        t.Performers   = _trackPerformers.Count > 0 ? [.. _trackPerformers] : null;
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
        //   • field StartedMixed AND user didn't touch (IsMixed still true), or
        //   • field StartedMixed AND user cleared the placeholder without retyping
        //     (Value is empty — don't wipe every track's TrackNumber).
        // Otherwise validate the integer and write.
        if (!SkipMixedTextWrite(_vm.TrackNumber))
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

        // ── SPARS Code — skip if Mixed sentinel still selected ─────────────────
        if (!_mixedFields.Contains("SparsCode")
            || !SparsCodeCombo.IsMixedSentinelSelected(TrackSparsCodeBox))
        {
            var spars = SparsCodeCombo.GetValue(TrackSparsCodeBox);
            foreach (var t in _editTracks!) t.SparsCode = spars;
        }

        // ── Stereo — SelectedIndex 3 is the "Mixed" sentinel; skip if still there ─
        if (!_mixedFields.Contains("IsStereo") || TrackStereoBox.SelectedIndex != 3)
        {
            var stereo = TrackStereoBox.SelectedIndex == 1 ? (bool?)true
                       : TrackStereoBox.SelectedIndex == 2 ? false
                       : null;
            foreach (var t in _editTracks!) t.IsStereo = stereo;
        }

        // ── Session ───────────────────────────────────────────────────────────
        // ResolveSelection distinguishes three cases:
        //   • Real session selected → returns the session index.
        //   • "(no session)" selected → returns null + isMixedSentinel=false.
        //   • Multi-edit "Mixed" or "(multiple albums)" sentinel still selected
        //     → isMixedSentinel=true and we SKIP the write entirely.
        // The "(multiple albums — cannot edit)" combo state populates with
        // sessions.Count == 0, so the Mixed-sentinel index is correctly
        // computed at position 1 → also caught by mixedSentinelPresent.
        var sessionIdx = SessionIndexMapping.ResolveSelection(
            SessionBox.SelectedIndex, _sessions.Count,
            _sessionMixedSentinelIndex, out var isSessionMixedSentinel);
        if (!isSessionMixedSentinel)
        {
            foreach (var t in _editTracks!) t.SessionIndex = sessionIdx;
        }

        // ── Piece References ──────────────────────────────────────────────────
        // Apply when: not a mixed field (so it's a uniform list the user may have edited),
        // or the user touched the list (Add/Remove cleared _pieceRefsUntouched).
        if (!_mixedFields.Contains("PieceRefs") || !_pieceRefsUntouched)
        {
            var refs = _pieceRefs.Count > 0 ? _pieceRefs.ToList() : null;
            foreach (var t in _editTracks!) t.PieceRefs = refs;
        }

        // ── Performers ─────────────────────────────────────────────────────────
        // Apply when: not mixed (i.e. user is editing a known shared list), or
        // mixed but the user touched the list (Add/Remove cleared the flag).
        if (!_mixedFields.Contains("Performers") || !_performersUntouched)
        {
            var performers = _trackPerformers.Count > 0 ? _trackPerformers.ToList() : null;
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
        _pieceRefs.Add(dlg.SelectedRef);
    }

    private void OnRemovePieceRef(object sender, RoutedEventArgs e)
    {
        if (PieceRefList.SelectedItem is TrackPieceRef selected)
            _pieceRefs.Remove(selected);
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
    /// it in <see cref="_pieceRefs"/> at the same index so the ListBox
    /// re-evaluates <see cref="TrackPieceRef.DisplaySummary"/>.
    /// </summary>
    private void EditSelectedPieceRefDetails()
    {
        if (PieceRefList.SelectedItem is not TrackPieceRef selected) return;
        var idx = _pieceRefs.IndexOf(selected);
        if (idx < 0) return;

        var dlg = new PieceRefDetailsWindow(selected, _allPieces) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.Saved) return;

        // ObservableCollection's indexer raises a Replace event, which is the
        // signal the ListBox needs to re-render the row with the updated
        // DisplaySummary. Same instance — same identity — but the binding
        // refreshes.
        _pieceRefs[idx] = selected;
        PieceRefList.SelectedIndex = idx;
    }

    // ── Track performers ──────────────────────────────────────────────────────

    private void OnAddTrackPerformer(object sender, RoutedEventArgs e)
    {
        var dlg = new PerformerEditorWindow(null, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _trackPerformers.Add(dlg.Result);
    }

    private void OnRemoveTrackPerformer(object sender, RoutedEventArgs e)
    {
        if (TrackPerformerList.SelectedItem is AlbumPerformer selected)
            _trackPerformers.Remove(selected);
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
