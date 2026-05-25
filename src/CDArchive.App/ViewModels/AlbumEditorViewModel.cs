using CDArchive.App.Helpers;
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
    }

    private static void Init(MixedField<string> field, IEnumerable<string> values, string mixedPlaceholder)
    {
        var distinct = values.Distinct().ToList();
        if (distinct.Count == 1)
            field.InitUnanimous(distinct[0]);
        else
            field.InitMixed(mixedPlaceholder);
    }
}
