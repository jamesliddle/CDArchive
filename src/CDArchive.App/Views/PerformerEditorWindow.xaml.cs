using System.ComponentModel;
using System.Windows;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.App.Views;

public partial class PerformerEditorWindow : Window
{
    // H13 small-editors slice 3: field state moved to PerformerEditorViewModel.

    // Rework H31: hold a working instance and mutate it in place on OK rather
    // than constructing a fresh AlbumPerformer from the editor's three string
    // fields. Pre-fix any field this editor didn't surface (today there are
    // none; tomorrow there might be — and the AlbumPerformerRow already has
    // PersonId / EnsembleId FKs that future model work may expose) was
    // silently dropped on edit. Mutating preserves every non-edited field.
    private readonly PerformerEditorViewModel _vm = new();
    private readonly AlbumPerformer _working;

    // Map of role name → voice type for cast-derived roles only. Drives the
    // Instrument prefill when the user picks a role from the dropdown.
    // Comparison is case-insensitive so the editor matches what the user
    // sees in the dropdown regardless of capitalisation.
    private readonly Dictionary<string, string?> _voiceTypeByRole =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks the most recent voice-type value the editor wrote into
    // Instrument as a prefill. When the user changes Role again, we only
    // overwrite Instrument if it's still equal to this value (the user
    // accepted the prefill) or empty. Any deliberate edit by the user
    // displaces this and the prefill becomes sticky to their value.
    private string? _lastPrefilledInstrument;

    /// <summary>
    /// Controls whether the Role row is rendered. Hidden when the editor has
    /// no role suggestions to offer (no cast, no pick-list entries) AND the
    /// existing performer has no Role value of its own — so the orchestral-
    /// album common case shows a tighter dialog. Set once in the ctor before
    /// the visual tree renders; bound from XAML via RelativeSource.
    /// </summary>
    public bool ShowRole { get; private set; } = true;

    public AlbumPerformer? Result { get; private set; }

    // Held so OnOkClick can add novel Instrument values back to the shared
    // pick list. Mutations land on the same instance the AlbumEditor / Tracks
    // editor was handed; the surrounding save flow (vm.SaveAsync) persists it.
    private readonly CanonPickLists _pickLists;

    /// <summary>
    /// <paramref name="pickLists"/> — shared editor pick-lists; the Role
    /// dropdown reads PerformerRoles, the Instrument dropdown reads
    /// Instruments, and any novel Instrument value the user enters is added
    /// to the pick list on OK so it appears next time. <paramref name="castRoles"/>
    /// — character / cast roles (name + voice type) harvested from the canon
    /// pieces this album or track references; <see cref="PieceRoleCollector"/>
    /// produces these. Cast roles appear first in the Role dropdown; the
    /// pick-list entries follow. Picking a cast role with a known voice type
    /// prefills <see cref="AlbumPerformer.Instrument"/> with that voice type.
    /// </summary>
    public PerformerEditorWindow(
        AlbumPerformer? existing,
        CanonPickLists pickLists,
        IReadOnlyList<CastRole>? castRoles = null)
    {
        InitializeComponent();
        DataContext = _vm;

        _pickLists = pickLists;

        if (castRoles is not null)
            foreach (var r in castRoles)
                _voiceTypeByRole[r.Name] = r.VoiceType;

        RoleBox.ItemsSource       = MergeRoles(castRoles, pickLists.PerformerRoles);
        InstrumentBox.ItemsSource = pickLists.Instruments.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        _working = existing ?? new AlbumPerformer();
        _vm.LoadFromPerformer(_working);

        // Hide Role when there's nothing to choose and no value to preserve.
        // Keeping the row visible if the existing record HAS a Role avoids
        // silently truncating data the user can no longer see / edit.
        var hasAnyRoleSource =
            (castRoles is { Count: > 0 }) ||
            (pickLists.PerformerRoles is { Count: > 0 }) ||
            !string.IsNullOrWhiteSpace(_vm.Role);
        ShowRole = hasAnyRoleSource;

        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private static IReadOnlyList<string> MergeRoles(
        IReadOnlyList<CastRole>? castRoles, IReadOnlyList<string> pickListRoles)
    {
        if (castRoles is null or { Count: 0 }) return pickListRoles;

        var merged = new List<string>(castRoles.Count + pickListRoles.Count);
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in castRoles)
            if (!string.IsNullOrWhiteSpace(r.Name) && seen.Add(r.Name)) merged.Add(r.Name);
        foreach (var r in pickListRoles)
            if (!string.IsNullOrWhiteSpace(r) && seen.Add(r)) merged.Add(r);
        return merged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PerformerEditorViewModel.Role)) return;

        var role = _vm.Role?.Trim();
        if (string.IsNullOrEmpty(role)) return;

        // Only cast roles have a voice type to prefill from; free-typed
        // values + pick-list values aren't in the map and harmlessly no-op.
        if (!_voiceTypeByRole.TryGetValue(role, out var voiceType)) return;
        if (string.IsNullOrEmpty(voiceType)) return;

        var current = _vm.Instrument ?? "";
        var canOverwrite =
            current.Length == 0 ||
            string.Equals(current, _lastPrefilledInstrument, StringComparison.OrdinalIgnoreCase);
        if (!canOverwrite) return;

        _vm.Instrument = voiceType;
        _lastPrefilledInstrument = voiceType;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        var error = _vm.SaveToPerformer(_working);
        if (error == PerformerEditorViewModel.SaveValidationError.MissingName)
        {
            MessageBox.Show("Name is required.", "Validation",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            NameBox.Focus();
            return;
        }

        // If the user typed an Instrument value not already in the pick
        // list, add it so future opens of any Performer editor suggest it.
        // Mutates the shared CanonPickLists instance; the surrounding album /
        // track save flow persists it via SaveBatchAsync(pickLists:).
        var instrument = _working.Instrument?.Trim();
        if (!string.IsNullOrEmpty(instrument) &&
            !_pickLists.Instruments.Contains(instrument, StringComparer.OrdinalIgnoreCase))
        {
            _pickLists.Instruments.Add(instrument);
        }

        Result = _working;
        DialogResult = true;
    }
}
