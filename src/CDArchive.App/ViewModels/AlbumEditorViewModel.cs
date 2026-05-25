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
