using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Shared-field shuttle between <see cref="CanonPiece"/> and
/// <see cref="CanonPieceVersion"/> for the unified piece editor (H14).
///
/// <para>The editor only knows how to edit piece-shaped objects; when the
/// user opens a version, the editor calls <see cref="FromVersion"/> to
/// project the version into a transient <see cref="CanonPiece"/>, edits
/// it, and calls <see cref="IntoVersion"/> on OK to write the changes
/// back. Pre-fix the projection was duplicated as two ~25-line manual
/// property lists inside <c>PieceEditorWindow.xaml.cs</c>, with every
/// new model field requiring two coordinated edits — and one such field
/// (<see cref="CanonPiece.TextAuthor"/>) had already drifted out of sync
/// and was silently dropped on every version save.</para>
///
/// <para>The shared property set is enforced by
/// <c>PieceVersionShuttleTests</c>: a reflection-based test that
/// round-trips every property whose name + type matches between the two
/// model classes (modulo the explicit "piece-only" / "version-only"
/// exception lists) and asserts the value survives.</para>
/// </summary>
public static class PieceVersionShuttle
{
    /// <summary>
    /// Properties that exist on <see cref="CanonPiece"/> but not on
    /// <see cref="CanonPieceVersion"/> — piece-only state that doesn't
    /// participate in the shuttle.
    /// </summary>
    public static readonly IReadOnlySet<string> PieceOnlyPropertyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(CanonPiece.IsProvisional),    // versions inherit provisional from their owning piece
        nameof(CanonPiece.Versions),         // versions of a version aren't a thing
        nameof(CanonPiece.Arrangements),     // historical-only piece-level field
        nameof(CanonPiece.Cadenza),          // piece-level field
        nameof(CanonPiece.TitleNumber),      // piece-level field
    };

    /// <summary>
    /// Properties that exist on <see cref="CanonPieceVersion"/> but not on
    /// <see cref="CanonPiece"/> — version-only state managed by the editor
    /// outside the shuttle (typically a Description box bound directly to
    /// <c>_sourceVersion</c>).
    /// </summary>
    public static readonly IReadOnlySet<string> VersionOnlyPropertyNames = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(CanonPieceVersion.Description),
        nameof(CanonPieceVersion.ContributingComposers),
    };

    /// <summary>
    /// Project a version into a transient piece for editing.
    /// <paramref name="showSubpieceNumbersDefault"/> seeds the
    /// <see cref="CanonPiece.NumberedSubpieces"/> checkbox when the
    /// version has no explicit override: the editor's parent piece may
    /// have one default, the version may want to inherit it.
    /// </summary>
    public static CanonPiece FromVersion(CanonPieceVersion v, bool showSubpieceNumbersDefault)
    {
        var numbered = v.NumberedSubpieces;
        return new CanonPiece
        {
            Composer                = v.Composer,
            Composers               = v.Composers?.ToList(),
            Form                    = v.Form,
            Title                   = v.Title,
            TitleEnglish            = v.TitleEnglish,
            Subtitle                = v.Subtitle,
            Nickname                = v.Nickname,
            Number                  = v.Number,
            MusicNumber             = v.MusicNumber,
            KeyTonality             = v.KeyTonality,
            KeyMode                 = v.KeyMode,
            CatalogInfo             = v.CatalogInfo?.ToList(),
            InstrumentationCategory = v.InstrumentationCategory,
            Instrumentation         = v.Instrumentation,
            PublicationYear         = v.PublicationYear,
            CompositionYears        = v.CompositionYears,
            // Preserve explicit override; seed from parent's default when null
            // so the checkbox shows the right value.
            NumberedSubpieces       = numbered ?? (showSubpieceNumbersDefault ? null : false),
            SubpiecesStart          = v.SubpiecesStart,
            Notes                   = v.Notes,
            Variants                = v.Variants?.ToList(),
            TextAuthor              = v.TextAuthor,   // H14: previously dropped on edit
            Roles                   = v.Roles,
            // Markers are reference-shared (not cloned) so their stable Ids
            // travel into the piece editor and back out on save.
            Markers                 = v.Markers?.ToList(),
            Subpieces               = v.Subpieces?.ToList(),
        };
    }

    /// <summary>
    /// Write the edited piece-shaped fields back into the source version.
    /// Version-only fields (Description, ContributingComposers) are left
    /// untouched — the editor manages Description separately, and the
    /// editor doesn't expose ContributingComposers at all (so its
    /// pre-edit value survives unchanged).
    /// </summary>
    public static void IntoVersion(CanonPiece p, CanonPieceVersion v)
    {
        v.Composer                = p.Composer;
        v.Composers               = p.Composers;
        v.Form                    = p.Form;
        v.Title                   = p.Title;
        v.TitleEnglish            = p.TitleEnglish;
        v.Subtitle                = p.Subtitle;
        v.Nickname                = p.Nickname;
        v.Number                  = p.Number;
        v.MusicNumber             = p.MusicNumber;
        v.KeyTonality             = p.KeyTonality;
        v.KeyMode                 = p.KeyMode;
        v.CatalogInfo             = p.CatalogInfo;
        v.InstrumentationCategory = p.InstrumentationCategory;
        v.Instrumentation         = p.Instrumentation;
        v.PublicationYear         = p.PublicationYear;
        v.CompositionYears        = p.CompositionYears;
        v.NumberedSubpieces       = p.NumberedSubpieces;
        v.SubpiecesStart          = p.SubpiecesStart;
        v.Notes                   = p.Notes;
        v.Variants                = p.Variants;
        v.TextAuthor              = p.TextAuthor;     // H14: previously dropped on save
        v.Roles                   = p.Roles;
        v.Markers                 = p.Markers;
        v.Subpieces               = p.Subpieces;
    }
}
