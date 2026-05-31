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
using CDArchive.Core.Models;
using Microsoft.Win32;

namespace CDArchive.App.Views;

public partial class TrackEditorWindow : Window
{
    // Disc context — the window operates directly on this disc's Tracks list
    private readonly AlbumDisc? _disc;
    private int _trackIndex;                      // index into _disc.Tracks; >= Count means "adding new"

    private readonly CanonPickLists           _pickLists;
    private readonly IReadOnlyList<CanonPiece> _allPieces;

    // Album whose session fields supply defaults when the track's own
    // session fields are blank. Set in single/multi/new ctors when there's
    // a parent album; null in loose-track mode and in across-album multi-edit.
    private readonly CanonAlbum? _defaultsFromAlbum;

    // ── Multi-edit state ──────────────────────────────────────────────────────

    private readonly bool _isMixed;
    private readonly IReadOnlyList<AlbumTrack>? _editTracks;
    // True when every track in _editTracks is a loose track (no owning album).
    // Hides TrackNumber UI and gates SaveMulti's write of that field.
    private readonly bool _allLooseBatch;

    // ── Loose-track state ─────────────────────────────────────────────────────
    // Loose mode is signalled by _disc == null (and !_isMixed). The track
    // instance lives in _looseTrack; the consolidated single-track ctor
    // routes to SaveLoose when this is non-null.
    private readonly AlbumTrack? _looseTrack;
    private bool IsLooseTrack => _looseTrack != null;

    // ── Audio-file duration sync ──────────────────────────────────────────────
    private TimeSpan? _flacFileDuration;
    private TimeSpan? _mp3FileDuration;
    private bool      _audioDurationMismatch;
    private static readonly TimeSpan DurationAgreementTolerance = TimeSpan.FromSeconds(1);

    // Rework H22 — snapshots so OnClosing can roll back disc mutations on
    // Cancel. Session-list snapshot retired in the sessions-as-fields refactor
    // (no shared session list any more; per-track session fields are written
    // only on OK via SaveSingle, like every other track field).
    private readonly List<AlbumTrack>? _tracksSnapshotForRollback;

    private bool IsAddingNew => !_isMixed && !IsLooseTrack && _disc != null && _trackIndex >= _disc.Tracks.Count;

    private readonly TrackEditorViewModel _vm = new();

    // ── Mode-driven visibility (H18) ──────────────────────────────────────────
    public bool ShowNavigation     { get; private set; }
    public bool ShowTrackNumber    { get; private set; }
    public bool ShowSessionFields  { get; private set; }
    public bool ShowSessionLists   { get; private set; }

    // ── Constructor: single track (disc-bound or loose) ──────────────────────
    //
    // Disc-bound mode: pass <paramref name="disc"/> + <paramref name="trackIndex"/>;
    // leave <paramref name="looseTrack"/> null. When <c>trackIndex >= disc.Tracks.Count</c>
    // the editor is in "add new" mode (a fresh track is appended on save).
    //
    // Loose mode: pass <paramref name="looseTrack"/> (the standalone track
    // instance); leave <paramref name="disc"/> null and <paramref name="trackIndex"/>
    // at -1. Track # and Prev/Next nav are hidden; save goes through
    // <see cref="TrackEditorViewModel.SaveLoose"/> so TrackNumber stays at
    // the 0 sentinel.

    public TrackEditorWindow(
        AlbumDisc? disc,
        int trackIndex,
        AlbumTrack? looseTrack,
        CanonPickLists pickLists,
        IReadOnlyList<CanonPiece> allPieces,
        CanonAlbum? defaultsFromAlbum)
    {
        if (disc == null && looseTrack == null)
            throw new ArgumentException("Either disc or looseTrack must be supplied.");
        if (disc != null && looseTrack != null)
            throw new ArgumentException("disc and looseTrack are mutually exclusive.");

        InitializeComponent();
        DataContext = _vm;

        _disc              = disc;
        _trackIndex        = disc != null ? trackIndex : -1;
        _looseTrack        = looseTrack;
        _pickLists         = pickLists;
        _allPieces         = allPieces;
        _defaultsFromAlbum = defaultsFromAlbum;
        _isMixed           = false;

        var loose = looseTrack != null;
        ShowNavigation    = !loose;
        ShowTrackNumber   = !loose;
        ShowSessionFields = true;
        ShowSessionLists  = true;

        // Snapshot the disc's track list so Cancel rolls back any Prev/Next
        // per-step commits. Not relevant for loose (no disc, no Prev/Next).
        if (disc != null)
            _tracksSnapshotForRollback = DeepClone(disc.Tracks);

        PieceRefList.ItemsSource       = _vm.PieceRefs.Items;
        TrackPerformerList.ItemsSource = _vm.Performers.Items;

        // Always subscribe Closing: the handler body is a no-op when there's
        // no snapshot to roll back, so this is safe for the loose path too.
        Closing += TrackEditorWindow_Closing;
        SubscribeAudioPathChanges();

        if (loose)
            Title = "Edit Track";

        LoadTrack();
        RefreshDurationFromAudioFiles();
    }

    // ── Constructor: multiple tracks (bulk edit) ──────────────────────────────

    public TrackEditorWindow(
        IReadOnlyList<AlbumTrack> tracks,
        CanonPickLists pickLists,
        IReadOnlyList<CanonPiece> allPieces,
        CanonAlbum? defaultsFromAlbum,
        bool allLoose = false)
    {
        InitializeComponent();
        DataContext = _vm;

        _disc              = null;
        _trackIndex        = -1;
        _pickLists         = pickLists;
        _allPieces         = allPieces;
        _defaultsFromAlbum = defaultsFromAlbum;
        _isMixed           = true;
        _editTracks        = tracks;
        _allLooseBatch     = allLoose;

        ShowNavigation    = false;
        ShowTrackNumber   = !allLoose;
        // Session text fields stay visible in multi-edit (with the same
        // Mixed-placeholder shape as the other text fields). The Engineers
        // / Producers lists do NOT bulk-edit meaningfully — hidden.
        ShowSessionFields = true;
        ShowSessionLists  = false;

        PieceRefList.ItemsSource       = _vm.PieceRefs.Items;
        TrackPerformerList.ItemsSource = _vm.Performers.Items;

        Closing += TrackEditorWindow_Closing;

        Title = $"Edit {tracks.Count} Tracks";

        PopulateMultiFields();
    }

    // ── Multi-edit: populate every field with unanimous value or "Mixed" ─────

    private void PopulateMultiFields()
    {
        var tracks = _editTracks!;
        _vm.LoadMulti(tracks, MixedPlaceholder.PlaceholderText);

        if (_vm.TrackNumber.IsMixed) MixedPlaceholder.Apply(TrackNumberBox);
        if (_vm.Duration.IsMixed)    MixedPlaceholder.Apply(DurationBox);
        if (_vm.Description.IsMixed) MixedPlaceholder.Apply(DescriptionBox);

        if (_vm.SessionDates.IsMixed)   MixedPlaceholder.Apply(SessionDatesBox);
        if (_vm.SessionVenue.IsMixed)   MixedPlaceholder.Apply(SessionVenueBox);
        if (_vm.SessionCity.IsMixed)    MixedPlaceholder.Apply(SessionCityBox);
        if (_vm.SessionState.IsMixed)   MixedPlaceholder.Apply(SessionStateBox);
        if (_vm.SessionCountry.IsMixed) MixedPlaceholder.Apply(SessionCountryBox);

        if (_vm.SparsCode.IsMixed)
            SparsCodeCombo.AppendMixedSentinel(TrackSparsCodeBox);
        else
            SparsCodeCombo.SelectValue(TrackSparsCodeBox, _vm.SparsCode.Value);

        SetStereoComboFromVm();

        // Audio overrides aren't bulk-editable.
        AudioOverridesGroup.IsEnabled = false;
        AudioOverridesGroup.ToolTip   = "Audio file overrides are per-track and can't be bulk-edited.";

        if (_vm.PieceRefs.StartedMixed)
            PieceRefsMixedNote.Visibility = Visibility.Visible;
        if (_vm.Performers.StartedMixed)
            PerformerMixedNote.Visibility = Visibility.Visible;
    }

    // ── Combobox sync (SPARS Code / Stereo) ───────────────────────────────────

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
            _        => 0,
        };
    }

    // ── Track loading ─────────────────────────────────────────────────────────

    private void LoadTrack()
    {
        if (IsLooseTrack)
        {
            // LoadLoose was retired; LoadSingle(track, defaultsFromAlbum: null)
            // produces the same VM state (loose tracks have TrackNumber=0 in
            // storage, so the resulting VM TrackNumber is "0" — the sentinel
            // the Track # field would have hidden anyway).
            _vm.LoadSingle(_looseTrack!, defaultsFromAlbum: null);
        }
        else if (IsAddingNew)
        {
            _vm.LoadNew(_disc!, _defaultsFromAlbum);
        }
        else
        {
            _vm.LoadSingle(_disc!.Tracks[_trackIndex], _defaultsFromAlbum);
        }

        SparsCodeCombo.SelectValue(TrackSparsCodeBox, _vm.SparsCode.Value);
        SetStereoComboFromVm();

        UpdateTitleAndButtons();
    }

    private void UpdateTitleAndButtons()
    {
        if (IsLooseTrack)
        {
            // Title was set once in the ctor; Prev/Next is hidden — nothing to
            // refresh here.
            return;
        }

        // Title is uniform across the loose and album-bound single-edit modes
        // ("Edit Track"); the add-new path keeps its distinct verb. Multi-edit
        // sets its own title in the bulk ctor ("Edit N Tracks").
        Title = IsAddingNew ? "Add Track" : "Edit Track";

        PrevButton.IsEnabled = _trackIndex > 0;
        NextButton.IsEnabled = IsAddingNew || _trackIndex < _disc!.Tracks.Count - 1;
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        if (_trackIndex <= 0) return;
        if (!CheckAudioDurationConsistency()) return;
        if (!HandleSaveValidationError(_vm.SaveSingle(_disc!, _trackIndex))) return;
        _trackIndex--;
        LoadTrack();
        RefreshDurationFromAudioFiles();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (!CheckAudioDurationConsistency()) return;
        if (!HandleSaveValidationError(_vm.SaveSingle(_disc!, _trackIndex))) return;

        _trackIndex++;
        LoadTrack();
        RefreshDurationFromAudioFiles();
    }

    private bool HandleSaveValidationError(TrackEditorViewModel.SaveValidationError error)
    {
        if (error == TrackEditorViewModel.SaveValidationError.InvalidTrackNumber)
        {
            MessageBox.Show("Track number must be a positive integer.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TrackNumberBox.Focus();
            return false;
        }
        return true;
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
            Title           = title,
            Filter          = filter,
            DefaultExt      = defaultExt,
            CheckFileExists = true,
        };
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

    // ── Engineers / Producers list handlers ──────────────────────────────────

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

    private void AddNameTo(ObservableCollection<string> list, ListBox listBox,
                            string title, string prompt)
    {
        var dlg = new NameInputWindow(title, prompt) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list.Add(dlg.Result);
        listBox.SelectedIndex = list.Count - 1;
    }

    private void EditSelectedNameIn(ObservableCollection<string> list, ListBox listBox,
                                     string title, string prompt)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        var dlg = new NameInputWindow(title, prompt, list[idx]) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;
        list[idx] = dlg.Result;
        listBox.SelectedIndex = idx;
    }

    private static void RemoveSelectedNameIn(ObservableCollection<string> list, ListBox listBox)
    {
        var idx = listBox.SelectedIndex;
        if (idx < 0 || idx >= list.Count) return;
        list.RemoveAt(idx);
        if (list.Count > 0) listBox.SelectedIndex = Math.Min(idx, list.Count - 1);
    }

    private static void MoveSelectedNameIn(ObservableCollection<string> list, ListBox listBox, int delta)
    {
        var idx = listBox.SelectedIndex;
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= list.Count) return;
        list.Move(idx, target);
        listBox.SelectedIndex = target;
    }

    private static void UpdateNameListButtons(
        ListBox listBox, ObservableCollection<string> list,
        Button editBtn, Button removeBtn, Button upBtn, Button downBtn)
    {
        var idx = listBox.SelectedIndex;
        var has = idx >= 0;
        editBtn.IsEnabled   = has;
        removeBtn.IsEnabled = has;
        upBtn.IsEnabled     = has && idx > 0;
        downBtn.IsEnabled   = has && idx < list.Count - 1;
    }

    // ── OK ────────────────────────────────────────────────────────────────────

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (!CheckAudioDurationConsistency()) return;

        TrackEditorViewModel.SaveValidationError error;
        if (_isMixed)
        {
            error = _vm.SaveMulti(_editTracks!, _allLooseBatch);
        }
        else if (IsLooseTrack)
        {
            _vm.SaveLoose(_looseTrack!);
            error = TrackEditorViewModel.SaveValidationError.None;
        }
        else
        {
            error = _vm.SaveSingle(_disc!, _trackIndex);
        }

        if (!HandleSaveValidationError(error)) return;
        DialogResult = true;
    }

    // ── Audio-file duration sync ──────────────────────────────────────────────

    private void SubscribeAudioPathChanges()
    {
        _vm.FlacPath.PropertyChanged += OnAudioPathChanged;
        _vm.Mp3Path.PropertyChanged  += OnAudioPathChanged;
    }

    private void OnAudioPathChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MixedField<string>.Value)) return;
        RefreshDurationFromAudioFiles();
    }

    private void RefreshDurationFromAudioFiles()
    {
        if (_isMixed) return;

        _flacFileDuration = AudioFileDuration.Read(_vm.FlacPath.Value);
        _mp3FileDuration  = AudioFileDuration.Read(_vm.Mp3Path.Value);
        _audioDurationMismatch = false;

        if (_flacFileDuration is null && _mp3FileDuration is null)
        {
            DurationBox.IsReadOnly = false;
            DurationBox.Background = Brushes.White;
            DurationBox.ToolTip    = "e.g. 5:32 or 1:02:15";
            return;
        }

        DurationBox.IsReadOnly = true;

        if (_flacFileDuration is { } f && _mp3FileDuration is { } m)
        {
            var diff = (f - m).Duration();
            _vm.Duration.Value = AudioFileDuration.Format(f);
            if (diff <= DurationAgreementTolerance)
            {
                DurationBox.Background = Brushes.WhiteSmoke;
                DurationBox.ToolTip    = $"Read from FLAC ({AudioFileDuration.Format(f)}); MP3 length {AudioFileDuration.Format(m)} agrees within tolerance.";
            }
            else
            {
                DurationBox.Background = Brushes.MistyRose;
                DurationBox.ToolTip    = $"Mismatch — FLAC: {AudioFileDuration.Format(f)}  •  MP3: {AudioFileDuration.Format(m)}. Saving is blocked until one of the paths is fixed.";
                _audioDurationMismatch = true;
            }
        }
        else
        {
            var only = _flacFileDuration ?? _mp3FileDuration!.Value;
            var which = _flacFileDuration is not null ? "FLAC" : "MP3";
            _vm.Duration.Value = AudioFileDuration.Format(only);
            DurationBox.Background = Brushes.WhiteSmoke;
            DurationBox.ToolTip    = $"Read from the {which} audio file.";
        }
    }

    private bool CheckAudioDurationConsistency()
    {
        if (!_audioDurationMismatch) return true;
        var f = AudioFileDuration.Format(_flacFileDuration!.Value);
        var m = AudioFileDuration.Format(_mp3FileDuration!.Value);
        MessageBox.Show(this,
            $"The FLAC and MP3 files for this track have different lengths " +
            $"(FLAC: {f}, MP3: {m}). Fix one of the paths before saving.",
            "Audio length mismatch", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

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

    private void EditSelectedPieceRefDetails()
    {
        if (PieceRefList.SelectedItem is not TrackPieceRef selected) return;
        var idx = _vm.PieceRefs.Items.IndexOf(selected);
        if (idx < 0) return;

        var dlg = new PieceRefDetailsWindow(selected, _allPieces) { Owner = this };
        if (dlg.ShowDialog() != true || !dlg.Saved) return;

        _vm.PieceRefs.Items[idx] = selected;
        PieceRefList.SelectedIndex = idx;
    }

    // ── Track performers ──────────────────────────────────────────────────────
    // Consistent shape with the AlbumEditor's Performers list: Add/Edit/
    // Remove/Up/Down on a vertical button stack to the right.

    private void OnAddTrackPerformer(object sender, RoutedEventArgs e)
    {
        var dlg = new PerformerEditorWindow(null, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _vm.Performers.Items.Add(dlg.Result);
        TrackPerformerList.SelectedIndex = _vm.Performers.Items.Count - 1;
    }

    private void OnEditTrackPerformer(object sender, RoutedEventArgs e)
    {
        if (TrackPerformerList.SelectedItem is not AlbumPerformer selected) return;
        var idx = _vm.Performers.Items.IndexOf(selected);
        var dlg = new PerformerEditorWindow(selected, _pickLists.PerformerRoles) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        _vm.Performers.Items[idx] = dlg.Result;
        TrackPerformerList.SelectedIndex = idx;
    }

    private void OnRemoveTrackPerformer(object sender, RoutedEventArgs e)
    {
        var idx = TrackPerformerList.SelectedIndex;
        if (idx < 0 || idx >= _vm.Performers.Items.Count) return;
        _vm.Performers.Items.RemoveAt(idx);
        if (_vm.Performers.Items.Count > 0)
            TrackPerformerList.SelectedIndex = Math.Min(idx, _vm.Performers.Items.Count - 1);
    }

    private void OnTrackPerformerMoveUp(object sender, RoutedEventArgs e) =>
        MoveTrackPerformer(delta: -1);

    private void OnTrackPerformerMoveDown(object sender, RoutedEventArgs e) =>
        MoveTrackPerformer(delta: +1);

    private void MoveTrackPerformer(int delta)
    {
        var idx = TrackPerformerList.SelectedIndex;
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= _vm.Performers.Items.Count) return;
        _vm.Performers.Items.Move(idx, target);
        TrackPerformerList.SelectedIndex = target;
    }

    private void OnTrackPerformerDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackPerformerList.SelectedIndex < 0) return;
        OnEditTrackPerformer(sender, e);
    }

    private void OnTrackPerformerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var idx = TrackPerformerList.SelectedIndex;
        var has = idx >= 0;
        EditTrackPerformerButton.IsEnabled   = has;
        RemoveTrackPerformerButton.IsEnabled = has;
        TrackPerformerUpButton.IsEnabled     = has && idx > 0;
        TrackPerformerDownButton.IsEnabled   = has && idx < _vm.Performers.Items.Count - 1;
    }

    // ── Cancel-rollback (Rework H22) ─────────────────────────────────────────

    private static List<T> DeepClone<T>(IEnumerable<T> source)
    {
        var json = JsonSerializer.Serialize(source.ToList());
        return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
    }

    private void TrackEditorWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DialogResult == true) return;
        if (_disc != null && _tracksSnapshotForRollback != null)
        {
            _disc.Tracks.Clear();
            foreach (var t in _tracksSnapshotForRollback) _disc.Tracks.Add(t);
        }
    }
}
