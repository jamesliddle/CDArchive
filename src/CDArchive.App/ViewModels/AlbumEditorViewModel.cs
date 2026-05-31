using System.Collections.ObjectModel;
using CDArchive.App.Helpers;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>AlbumEditorWindow</c> (H13, slice 1 — text fields).
///
/// <para>This is the first slice of the multi-PR H13 extraction. Owns the simple
/// album-level text fields (Title, Subtitle, Label, CatalogueNumber, Barcode,
/// ArchiveFolder, Notes). The combobox-driven fields (SparsCode, IsStereo) and
/// list-shaped fields (Performers, Sessions, Discs/Tracks) stay in code-behind
/// for now — they'll migrate in subsequent slices.</para>
///
/// <para>Multi-edit semantics are modelled via <see cref="MixedField{T}"/>: each
/// text field carries its own Value / IsMixed / WasEdited tri-state. XAML
/// TextBoxes TwoWay-bind to <c>FieldName.Value</c>; the editor's code-behind
/// reads <c>FieldName.WasEdited</c> on multi-edit save to decide whether to
/// commit. See the type doc on <see cref="MixedField{T}"/>.</para>
/// </summary>
public partial class AlbumEditorViewModel : ObservableObject
{
    /// <summary>Album title. Required in single-edit (validated on Save).</summary>
    public MixedField<string> Title           { get; } = new();
    public MixedField<string> Subtitle        { get; } = new();
    public MixedField<string> Label           { get; } = new();
    public MixedField<string> CatalogueNumber { get; } = new();
    public MixedField<string> Barcode         { get; } = new();
    public MixedField<string> ArchiveFolder   { get; } = new();
    public MixedField<string> Notes           { get; } = new();

    // ── Combobox fields (slice 2) ─────────────────────────────────────────────
    // SparsCode + IsStereo use a stable string vocabulary on the VM side; the
    // editor's code-behind syncs the (non-editable) ComboBox SelectedItems
    // imperatively because they use a "Mixed" sentinel ComboBoxItem rather
    // than a placeholder text — different shape from text fields.

    /// <summary>
    /// SPARS code. Values: "DDD" / "ADD" / "AAD" / "Unknown" (canonical set
    /// from the dropdown), or a legacy non-standard code string from existing
    /// data, or <see cref="SparsCodeMixedSentinel"/> when a multi-edit
    /// selection has differing values and the user hasn't picked one yet.
    /// Null / empty maps to "Unknown" on load.
    /// </summary>
    public MixedField<string> SparsCode { get; } = new();

    /// <summary>
    /// Stereo flag, represented as a string for the ComboBox sync. Values:
    /// "Unknown" / "Stereo" / "Mono" (the dropdown items), or
    /// <see cref="IsStereoMixedSentinel"/> when multi-edit values differ.
    /// </summary>
    public MixedField<string> IsStereo { get; } = new();

    /// <summary>Constant used on the VM side for the multi-edit "Mixed" SparsCode sentinel.</summary>
    public const string SparsCodeMixedSentinel = "Mixed";
    /// <summary>Constant used on the VM side for the multi-edit "Mixed" IsStereo sentinel.</summary>
    public const string IsStereoMixedSentinel = "Mixed";

    // String ↔ bool? translation for IsStereo. Keeps the VM string-typed
    // (matches the ComboBox vocabulary) and confines the conversion to a
    // single pair of helpers used at load and save time.
    public static string IsStereoToString(bool? v) => v switch
    {
        true  => "Stereo",
        false => "Mono",
        null  => "Unknown",
    };

    public static bool? IsStereoFromString(string? v) => v switch
    {
        "Stereo" => true,
        "Mono"   => false,
        _        => null,   // includes "Unknown", "" / null, AND the Mixed sentinel
    };

    public static string SparsCodeToString(string? v) =>
        string.IsNullOrEmpty(v) ? "Unknown" : v;

    public static string? SparsCodeFromString(string? v) =>
        string.IsNullOrEmpty(v) ? null : v;

    // ── Recording-session fields (post-session-as-fields refactor) ───────────
    // Pre-refactor an album owned a List<RecordingSession> and tracks
    // referenced one by Id. Now one set of session fields lives directly on
    // the album (and a parallel set on each track, defaulted from the album
    // on TrackEditor open). The first five are MixedField<string>s for the
    // multi-edit shape; Engineers and Producers are ObservableCollection<string>
    // mirroring the AlbumEditor's Performers shape — Add/Edit/Remove/Up/Down
    // handlers in the editor's code-behind drive them.

    public MixedField<string> SessionDates    { get; } = new();
    public MixedField<string> SessionVenue    { get; } = new();
    public MixedField<string> SessionCity     { get; } = new();
    public MixedField<string> SessionState    { get; } = new();
    public MixedField<string> SessionCountry  { get; } = new();

    // ── List-shaped fields ────────────────────────────────────────────────────
    // Performers and the two session name lists (Engineers / Producers) live
    // as ObservableCollections so the ListView ItemsSource bindings auto-
    // update on Add/Edit/Remove without manual reset cycles. Add/Edit/Remove
    // button handlers stay in the editor's code-behind because they open
    // modal child dialogs that need Window.GetWindow(this) as Owner.
    //
    // The Performers section + session fields are all hidden in multi-edit
    // (per H18's ShowPerformersSection / ShowSessionsSection visibility
    // bindings — multi-edit collapses the section entirely). LoadMulti
    // leaves these empty; SaveMulti doesn't read them.

    public ObservableCollection<AlbumPerformer> Performers        { get; } = new();
    public ObservableCollection<string>         SessionEngineers  { get; } = new();
    public ObservableCollection<string>         SessionProducers  { get; } = new();

    /// <summary>
    /// Populate from a single album (single-edit mode). Every field becomes
    /// Unanimous with the album's current value; <see cref="MixedField{T}.WasEdited"/>
    /// resets to false. Subsequent user edits via TwoWay bindings will trip
    /// <see cref="MixedField{T}.WasEdited"/>, but single-edit save commits
    /// every field unconditionally anyway — the flag is mostly for symmetry
    /// with multi-edit.
    /// </summary>
    public void LoadSingle(CanonAlbum album)
    {
        Title.InitUnanimous(album.Title           ?? "");
        Subtitle.InitUnanimous(album.Subtitle     ?? "");
        Label.InitUnanimous(album.Label           ?? "");
        CatalogueNumber.InitUnanimous(album.CatalogueNumber ?? "");
        Barcode.InitUnanimous(album.Barcode       ?? "");
        ArchiveFolder.InitUnanimous(album.ArchiveFolder ?? "");
        Notes.InitUnanimous(album.Notes           ?? "");
        SparsCode.InitUnanimous(SparsCodeToString(album.SparsCode));
        IsStereo.InitUnanimous(IsStereoToString(album.IsStereo));

        SessionDates.InitUnanimous(album.SessionDates       ?? "");
        SessionVenue.InitUnanimous(album.SessionVenue       ?? "");
        SessionCity.InitUnanimous(album.SessionCity         ?? "");
        SessionState.InitUnanimous(album.SessionState       ?? "");
        SessionCountry.InitUnanimous(album.SessionCountry   ?? "");

        Performers.Clear();
        if (album.Performers is { Count: > 0 } perfs)
            foreach (var p in perfs) Performers.Add(p);

        SessionEngineers.Clear();
        if (album.SessionEngineers is { Count: > 0 } engs)
            foreach (var e in engs) SessionEngineers.Add(e);

        SessionProducers.Clear();
        if (album.SessionProducers is { Count: > 0 } prods)
            foreach (var p in prods) SessionProducers.Add(p);
    }

    /// <summary>
    /// Populate from a multi-album selection (multi-edit mode). Each field
    /// inspects the distinct values across <paramref name="albums"/>: when
    /// all share one value it loads Unanimous; otherwise it loads Mixed with
    /// the supplied placeholder string. <see cref="MixedField{T}.WasEdited"/>
    /// resets to false; the editor's multi-edit save path then commits only
    /// the fields where the user actually touched the value (WasEdited == true).
    /// </summary>
    /// <param name="mixedPlaceholder">
    /// String to put in <c>Value</c> when the field is Mixed. Typically
    /// <see cref="MixedPlaceholder.PlaceholderText"/> (the literal "Mixed").
    /// Kept as a parameter so tests can inject something distinguishable.
    /// </param>
    public void LoadMulti(IReadOnlyList<CanonAlbum> albums, string mixedPlaceholder)
    {
        Init(Title,           albums.Select(a => a.Title           ?? ""), mixedPlaceholder);
        Init(Subtitle,        albums.Select(a => a.Subtitle        ?? ""), mixedPlaceholder);
        Init(Label,           albums.Select(a => a.Label           ?? ""), mixedPlaceholder);
        Init(CatalogueNumber, albums.Select(a => a.CatalogueNumber ?? ""), mixedPlaceholder);
        Init(Barcode,         albums.Select(a => a.Barcode         ?? ""), mixedPlaceholder);
        Init(ArchiveFolder,   albums.Select(a => a.ArchiveFolder   ?? ""), mixedPlaceholder);
        Init(Notes,           albums.Select(a => a.Notes           ?? ""), mixedPlaceholder);

        // Comboboxes use their own sentinel constants (the ComboBoxItem
        // sentinel rendering is view-side; the VM just tracks the value
        // string the user would see selected).
        Init(SparsCode, albums.Select(a => SparsCodeToString(a.SparsCode)), SparsCodeMixedSentinel);
        Init(IsStereo,  albums.Select(a => IsStereoToString(a.IsStereo)),  IsStereoMixedSentinel);

        // Session text fields share the multi-edit Mixed/Unanimous contract
        // with the other free-text fields.
        Init(SessionDates,   albums.Select(a => a.SessionDates   ?? ""), mixedPlaceholder);
        Init(SessionVenue,   albums.Select(a => a.SessionVenue   ?? ""), mixedPlaceholder);
        Init(SessionCity,    albums.Select(a => a.SessionCity    ?? ""), mixedPlaceholder);
        Init(SessionState,   albums.Select(a => a.SessionState   ?? ""), mixedPlaceholder);
        Init(SessionCountry, albums.Select(a => a.SessionCountry ?? ""), mixedPlaceholder);

        // Performers + Session Engineers/Producers sections are hidden in
        // multi-edit (H18 visibility binding); leave the collections empty.
        // SaveMulti doesn't write them in multi-edit.
        Performers.Clear();
        SessionEngineers.Clear();
        SessionProducers.Clear();
    }

    private static void Init(MixedField<string> field, IEnumerable<string> values, string mixedPlaceholder)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
            field.InitUnanimous(distinct[0]);
        else
            field.InitMixed(mixedPlaceholder);
    }

    // ── Save (slice 4) ────────────────────────────────────────────────────────
    // The single- and multi-edit save flows previously lived as `OnSaveClick`
    // + `SaveMulti` methods on the editor's code-behind, reading directly from
    // TextBox/ComboBox elements and tracking "did this field start Mixed" via
    // a per-window HashSet<string> _mixedFields. Slice 4 moves the data-mutation
    // pass into the VM: SaveSingle / SaveMulti operate on the album(s) directly
    // using only the VM's MixedField<T> state. The editor's code-behind retains
    // only the UI-bound bits (Title-required MessageBox + tab focus on
    // validation failure, DialogResult = true on success).
    //
    // The HashSet retires because MixedField<T>.StartedMixed (added in this
    // slice) covers the same information per-field.

    /// <summary>
    /// Single-edit validation result. <see cref="None"/> means the album was
    /// successfully mutated and the caller can close the dialog with success.
    /// </summary>
    public enum SaveValidationError
    {
        None,
        /// <summary>Title was empty/whitespace.</summary>
        MissingTitle,
    }

    /// <summary>
    /// H13 slice 4: single-edit save. Validates the Title is non-empty, then
    /// writes every VM field into <paramref name="album"/>, snapshots the
    /// Performers + Sessions ObservableCollections into the album's List
    /// fields, removes empty discs, and runs
    /// <see cref="AlbumFieldPropagator.Propagate"/> to push inheritable
    /// album-level fields down to every track.
    /// <para>Returns <see cref="SaveValidationError.None"/> on success;
    /// <see cref="SaveValidationError.MissingTitle"/> if the Title is blank
    /// (the album is then left unmutated and the caller is expected to surface
    /// a validation message + focus the Title field).</para>
    /// </summary>
    public SaveValidationError SaveSingle(CanonAlbum album, AlbumFieldPropagator.InheritableSnapshot originalInheritable)
    {
        var title = (Title.Value ?? "").Trim();
        if (string.IsNullOrEmpty(title)) return SaveValidationError.MissingTitle;

        album.Title           = title;
        album.Subtitle        = NullIfEmpty(Subtitle.Value);
        album.Label           = NullIfEmpty(Label.Value);
        album.CatalogueNumber = NullIfEmpty(CatalogueNumber.Value);
        album.Barcode         = NullIfEmpty(Barcode.Value);
        album.ArchiveFolder   = NullIfEmpty(ArchiveFolder.Value);
        album.SparsCode       = SparsCodeFromString(SparsCode.Value);
        album.Notes           = NullIfEmpty(Notes.Value);
        album.IsStereo        = IsStereoFromString(IsStereo.Value);

        // Snapshot the ObservableCollections to List<T> on save — the CanonAlbum
        // model fields are List<T>?, and storing the ObservableCollection instance
        // directly would be a type mismatch + would tie the model to a UI-facing
        // collection type.
        album.Performers       = Performers.Count       > 0 ? Performers.ToList()       : null;
        album.SessionEngineers = SessionEngineers.Count > 0 ? SessionEngineers.ToList() : null;
        album.SessionProducers = SessionProducers.Count > 0 ? SessionProducers.ToList() : null;

        // Session free-text fields.
        album.SessionDates   = NullIfEmpty(SessionDates.Value);
        album.SessionVenue   = NullIfEmpty(SessionVenue.Value);
        album.SessionCity    = NullIfEmpty(SessionCity.Value);
        album.SessionState   = NullIfEmpty(SessionState.Value);
        album.SessionCountry = NullIfEmpty(SessionCountry.Value);

        album.Discs.RemoveAll(d => d.Tracks.Count == 0);

        // Propagate inheritable album-level fields down to every track. The
        // propagator's snapshot vs current diff implements:
        //   • field changed → push to every track (overwrites prior overrides)
        //   • field unchanged → backfill null tracks only.
        AlbumFieldPropagator.Propagate(album, originalInheritable);

        return SaveValidationError.None;
    }

    /// <summary>
    /// H13 slice 4: multi-edit save. For each field, writes only when the user
    /// actually engaged with it:
    /// <list type="bullet">
    ///   <item>Text fields started Unanimous → always write (even empty —
    ///     the user's clear intent).</item>
    ///   <item>Text fields started Mixed AND user typed something → write.</item>
    ///   <item>Text fields started Mixed AND user did NOT type (still showing
    ///     placeholder, or cleared placeholder without retyping) → skip.
    ///     Critical: clearing-without-typing must not wipe every album to empty.</item>
    ///   <item>Combobox fields (SparsCode / IsStereo): started Unanimous or
    ///     user picked a value (cleared <see cref="MixedField{T}.IsMixed"/>)
    ///     → write. Started Mixed AND still Mixed → skip.</item>
    /// </list>
    /// Performers + Sessions are not editable in multi-edit (those tabs are
    /// hidden per H18); only the per-track null-backfill semantics apply.
    /// </summary>
    public void SaveMulti(IReadOnlyList<CanonAlbum> albums)
    {
        ApplyMixedFieldText(Title,           v => { foreach (var a in albums) a.Title           = v; });
        ApplyMixedFieldText(Subtitle,        v => { foreach (var a in albums) a.Subtitle        = v; });
        ApplyMixedFieldText(Label,           v => { foreach (var a in albums) a.Label           = v; });
        ApplyMixedFieldText(CatalogueNumber, v => { foreach (var a in albums) a.CatalogueNumber = v; });
        ApplyMixedFieldText(Barcode,         v => { foreach (var a in albums) a.Barcode         = v; });
        ApplyMixedFieldText(ArchiveFolder,   v => { foreach (var a in albums) a.ArchiveFolder   = v; });
        ApplyMixedFieldText(Notes,           v => { foreach (var a in albums) a.Notes           = v; });

        // Session text fields share the multi-edit contract. Engineers /
        // Producers (list-shaped) aren't editable in multi-edit so they
        // aren't written here — same shape as the Performers list above.
        ApplyMixedFieldText(SessionDates,   v => { foreach (var a in albums) a.SessionDates   = v; });
        ApplyMixedFieldText(SessionVenue,   v => { foreach (var a in albums) a.SessionVenue   = v; });
        ApplyMixedFieldText(SessionCity,    v => { foreach (var a in albums) a.SessionCity    = v; });
        ApplyMixedFieldText(SessionState,   v => { foreach (var a in albums) a.SessionState   = v; });
        ApplyMixedFieldText(SessionCountry, v => { foreach (var a in albums) a.SessionCountry = v; });

        // SparsCode + IsStereo: write IFF !(started Mixed AND still Mixed). For
        // a combo started Unanimous, IsMixed is false from the start so this
        // always passes. For one started Mixed, the SelectionChanged handler
        // sets the VM value (clearing IsMixed) when the user picks an item.
        var sparsTouched = !(SparsCode.StartedMixed && SparsCode.IsMixed);
        string? sparsValue = null;
        if (sparsTouched)
        {
            sparsValue = SparsCodeFromString(SparsCode.Value);
            foreach (var a in albums) a.SparsCode = sparsValue;
        }

        var stereoTouched = !(IsStereo.StartedMixed && IsStereo.IsMixed);
        bool? stereoValue = null;
        if (stereoTouched)
        {
            stereoValue = IsStereoFromString(IsStereo.Value);
            foreach (var a in albums) a.IsStereo = stereoValue;
        }

        // Track backfill: push album-level changes down + backfill nulls with
        // the album's current value. Mirrors AlbumFieldPropagator.Propagate but
        // here runs per-album in the multi-edit batch. Performers stays
        // per-album in multi-edit (the Performers tab is hidden) — backfill
        // nulls from each album's own performer list.
        foreach (var a in albums)
        {
            foreach (var disc in a.Discs)
            {
                foreach (var track in disc.Tracks)
                {
                    if (sparsTouched || track.SparsCode is null)
                        track.SparsCode = sparsTouched ? sparsValue : a.SparsCode;

                    if (stereoTouched || track.IsStereo is null)
                        track.IsStereo  = stereoTouched ? stereoValue : a.IsStereo;

                    if (track.Performers is null)
                        track.Performers = AlbumFieldPropagator.ClonePerformers(a.Performers);
                }
            }
        }
    }

    /// <summary>
    /// VM-side equivalent of the editor's pre-slice-4 <c>ApplyMixedFieldText</c>.
    /// Skips when the field is in the "user-didn't-engage" state for a
    /// previously-Mixed field; otherwise writes the trimmed-and-null-if-empty
    /// value through <paramref name="setter"/>.
    /// </summary>
    private static void ApplyMixedFieldText(MixedField<string> field, Action<string?> setter)
    {
        if (field.StartedMixed && (field.IsMixed || string.IsNullOrEmpty(field.Value)))
            return;
        setter(NullIfEmpty(field.Value));
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
