namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>piece_versions</c>. An alternative version or arrangement
/// of a parent piece. Versions don't nest versions — their subpieces live in
/// the <c>pieces</c> table with <see cref="PieceRow.ParentVersionId"/> set.
/// </summary>
public class PieceVersionRow
{
    public long Id { get; set; }

    public long PieceId { get; set; }
    public PieceRow Piece { get; set; } = null!;

    public int Position { get; set; }

    public string? Description { get; set; }

    public string? Title { get; set; }
    public string? TitleEnglish { get; set; }
    public string? Subtitle { get; set; }
    public string? Nickname { get; set; }
    public string? Form { get; set; }
    public int? Number { get; set; }
    public string? MusicNumber { get; set; }
    public string? KeyTonality { get; set; }
    public string? KeyMode { get; set; }
    public int? PublicationYear { get; set; }
    public string? InstrumentationCategory { get; set; }
    public bool? NumberedSubpieces { get; set; }
    public int? SubpiecesStart { get; set; }
    public string? Notes { get; set; }

    // ── Heterogeneous / free-shape fields stored as JSON text ────────────────
    public string? InstrumentationJson { get; set; }
    public string? CompositionYearsJson { get; set; }
    public string? TextAuthorJson { get; set; }
    public string? RolesJson { get; set; }
    public string? ArrangementsJson { get; set; }
    public string? CadenzaJson { get; set; }
    public string? TitleNumberJson { get; set; }
    public string? ContributingComposersJson { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────────
    public List<PieceRow> Subpieces { get; set; } = [];
    public List<PieceCatalogEntryRow> CatalogEntries { get; set; } = [];
    public List<PieceMarkerRow> Markers { get; set; } = [];
    public List<PieceComposerCreditRow> ComposerCredits { get; set; } = [];
    public List<PieceVariantRow> Variants { get; set; } = [];
    public List<AlbumTrackPieceRefRow> AlbumRefs { get; set; } = [];
}
