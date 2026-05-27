using System.Text.Json;
using CDArchive.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CDArchive.App.ViewModels;

/// <summary>
/// View-model for <c>PieceEditorWindow</c> (H13 PieceEditor extraction, slice 1).
///
/// <para>This is the first slice of the multi-PR PieceEditor extraction, following
/// the AlbumEditor + TrackEditor pattern. Owns the simple text fields:
/// Title, TitleEnglish, Subtitle, Nickname, Number (int? via string),
/// MusicNumber, PubYear (int? via string), CompYears (string ↔ JsonElement),
/// Notes, VersionDescription.</para>
///
/// <para>Unlike AlbumEditor and TrackEditor, the PieceEditor has <b>no multi-edit
/// mode</b> — only one piece / subpiece / version is edited at a time. So the
/// VM doesn't need <see cref="Helpers.MixedField{T}"/> /
/// <see cref="Helpers.MixedCollection{T}"/> wrappers; plain
/// <c>[ObservableProperty]</c> string fields suffice.</para>
///
/// <para>Integer fields (Number, PubYear) and the JSON-blob CompYears field are
/// stored as strings on the VM (matching the TextBox content) and converted at
/// load / save time. Empty/whitespace strings round-trip to null on the model.</para>
///
/// <para>Combobox fields (Composer, Form, KeyTonality, KeyMode, Category,
/// CatalogPrefix), the NumberedSubpieces checkbox + SubpiecesStart number field,
/// and all the list-shaped fields (Composers, CatalogEntries, Instruments,
/// Subpieces, Versions, Roles, Markers, Variants) stay in code-behind for now —
/// later slices migrate them.</para>
/// </summary>
public partial class PieceEditorViewModel : ObservableObject
{
    /// <summary>Piece title (original language).</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>English-language title (when distinct from the original).</summary>
    [ObservableProperty] private string _titleEnglish = "";

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _nickname = "";

    /// <summary>Work number within form (e.g. "9" for Symphony No. 9). Stored
    /// as string in the VM for TextBox binding; parsed to int? at save time.</summary>
    [ObservableProperty] private string _number = "";

    /// <summary>Traditional opera/oratorio numbering (e.g. "No. 14" within an act).</summary>
    [ObservableProperty] private string _musicNumber = "";

    /// <summary>Publication year. Stored as string for TextBox binding; parsed
    /// to int? at save time.</summary>
    [ObservableProperty] private string _pubYear = "";

    /// <summary>Composition years (may be a single year, a range, or a complex
    /// expression). Stored as string in the VM; serialised to a JSON string
    /// value at save time so the model's <see cref="JsonElement"/> field
    /// round-trips cleanly.</summary>
    [ObservableProperty] private string _compYears = "";

    /// <summary>Free-text notes.</summary>
    [ObservableProperty] private string _notes = "";

    /// <summary>Version description (e.g. "Original 1830 version", "arr. for piano").
    /// Visible only when the editor is in version mode.</summary>
    [ObservableProperty] private string _versionDescription = "";

    // ── Combobox fields (slice 2) ─────────────────────────────────────────────
    // The four editable combos (Composer, Form, KeyTonality, Category) bind
    // to `Text` so the user can either pick an existing entry or type a new
    // one. KeyMode is a non-editable combo with three fixed items
    // ("" / "major" / "minor") and binds to `SelectedValue` with
    // SelectedValuePath="Content". ItemsSource for the editable combos stays
    // in code-behind (it's view-side data — picklist references).

    /// <summary>Primary composer name. Editable combo bound to the
    /// pickLists-derived list of composer names.</summary>
    [ObservableProperty] private string _composer = "";

    /// <summary>Musical form (Sonata, Symphony, …). Editable combo bound to
    /// <c>pickLists.Forms</c>.</summary>
    [ObservableProperty] private string _form = "";

    /// <summary>Key tonality (C, B-flat, …). Editable combo bound to
    /// <c>pickLists.KeyTonalities</c>.</summary>
    [ObservableProperty] private string _keyTonality = "";

    /// <summary>Key mode ("" / "major" / "minor"). Non-editable combo,
    /// bound via SelectedValue.</summary>
    [ObservableProperty] private string _keyMode = "";

    /// <summary>Instrumentation category (Chamber, Piano, Orchestra, …).
    /// Editable combo bound to <c>pickLists.Categories</c>.</summary>
    [ObservableProperty] private string _category = "";

    /// <summary>
    /// Populate the VM's text fields from a piece. Combobox / checkbox / list
    /// state stays in the editor's code-behind for now — later slices will
    /// migrate those.
    /// </summary>
    public void LoadFromPiece(CanonPiece piece)
    {
        Title          = piece.Title           ?? "";
        TitleEnglish   = piece.TitleEnglish    ?? "";
        Subtitle       = piece.Subtitle        ?? "";
        Nickname       = piece.Nickname        ?? "";
        Number         = piece.Number?.ToString()         ?? "";
        MusicNumber    = piece.MusicNumber     ?? "";
        PubYear        = piece.PublicationYear?.ToString() ?? "";
        CompYears      = CompYearsToString(piece.CompositionYears);
        Notes          = piece.Notes           ?? "";

        // Combobox fields (slice 2).
        Composer       = piece.Composer                  ?? "";
        Form           = piece.Form                      ?? "";
        KeyTonality    = piece.KeyTonality               ?? "";
        KeyMode        = (piece.KeyMode ?? "").ToLowerInvariant();
        Category       = piece.InstrumentationCategory   ?? "";
        // VersionDescription is populated separately by the version ctor — see
        // LoadVersionDescription. The piece itself has no Description field.
    }

    /// <summary>
    /// LoadFromPiece supports an optional fallback for the Composer field
    /// (subpieces/versions inherit their parent's Composer when their own is
    /// blank). The editor's code-behind tracks this as <c>_inheritedComposer</c>
    /// and supplies it; the VM applies the fallback only when the piece's
    /// own Composer is empty.
    /// </summary>
    public void LoadFromPiece(CanonPiece piece, string? inheritedComposer)
    {
        LoadFromPiece(piece);
        if (string.IsNullOrEmpty(Composer) && !string.IsNullOrEmpty(inheritedComposer))
            Composer = inheritedComposer!;
    }

    /// <summary>Loads the version-only Description field. Called by the
    /// version ctor in addition to <see cref="LoadFromPiece"/>.</summary>
    public void LoadVersionDescription(CanonPieceVersion version)
    {
        VersionDescription = version.Description ?? "";
    }

    /// <summary>
    /// Write the VM's text fields back to the piece. Integer fields parse from
    /// string (failed parse → null); empty strings normalise to null. CompYears
    /// converts back to a JSON string element.
    /// </summary>
    public void SaveToPiece(CanonPiece piece)
    {
        piece.Title           = NullIfEmpty(Title);
        piece.TitleEnglish    = NullIfEmpty(TitleEnglish);
        piece.Subtitle        = NullIfEmpty(Subtitle);
        piece.Nickname        = NullIfEmpty(Nickname);
        piece.MusicNumber     = NullIfEmpty(MusicNumber);
        piece.Notes           = NullIfEmpty(Notes);

        piece.Number          = int.TryParse(Number.Trim(),  out var n) ? n : null;
        piece.PublicationYear = int.TryParse(PubYear.Trim(), out var y) ? y : null;
        piece.CompositionYears = StringToCompYears(CompYears);

        // Combobox fields (slice 2).
        piece.Composer                = NullIfEmpty(Composer);
        piece.Form                    = NullIfEmpty(Form);
        piece.KeyTonality             = NullIfEmpty(KeyTonality);
        piece.KeyMode                 = NullIfEmpty(KeyMode);
        piece.InstrumentationCategory = NullIfEmpty(Category);
    }

    /// <summary>Write the version-only Description back to the source version
    /// instance. Called by the editor's CopyPieceToVersion in addition to
    /// <see cref="SaveToPiece"/>.</summary>
    public void SaveVersionDescription(CanonPieceVersion version)
    {
        version.Description = NullIfEmpty(VersionDescription);
    }

    // ── Converters ────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract the string form of a CompositionYears JsonElement. When the
    /// element is a string, returns its value; otherwise uses
    /// <see cref="JsonElement.ToString"/> for the legacy/numeric/array shapes.
    /// </summary>
    public static string CompYearsToString(JsonElement? element)
    {
        if (element is not { } e) return "";
        return e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : e.ToString();
    }

    /// <summary>
    /// Wrap the user's CompYears text as a JSON string value. Empty / whitespace
    /// input maps to null. The result is a fresh <see cref="JsonElement"/> clone
    /// (so the underlying document isn't held).
    /// </summary>
    public static JsonElement? StringToCompYears(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Use JsonDocument.Parse on a quoted JSON string to safely escape any
        // embedded quotes / control chars; Clone() detaches from the parsed
        // document so it can be disposed.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s.Trim()));
        return doc.RootElement.Clone();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
