namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>pieces</c> table. A single table covers three roles:
/// <list type="bullet">
///   <item><b>Top-level piece</b> — <see cref="ParentPieceId"/> and <see cref="ParentVersionId"/> are both null.</item>
///   <item><b>Subpiece of a piece</b> — <see cref="ParentPieceId"/> is set.</item>
///   <item><b>Subpiece of a version</b> — <see cref="ParentVersionId"/> is set.</item>
/// </list>
/// A CHECK constraint in the schema ensures at most one parent is set.
/// Every subpiece therefore has a unique <see cref="Id"/> that album track
/// refs can point at directly, eliminating path-based identity.
/// </summary>
public class PieceRow
{
    public long Id { get; set; }

    public long ComposerId { get; set; }
    public ComposerRow Composer { get; set; } = null!;

    public long? ParentPieceId { get; set; }
    public PieceRow? ParentPiece { get; set; }

    public long? ParentVersionId { get; set; }
    public PieceVersionRow? ParentVersion { get; set; }

    /// <summary>Order within the parent (top-level pieces use composer-wide ordering).</summary>
    public int Position { get; set; }

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

    /// <summary>
    /// True until the piece is explicitly approved. Defaults to true so any
    /// row inserted without an explicit value (imports, iTunes auto-creates,
    /// historical rows back-filled by the schema migration) starts provisional.
    /// </summary>
    public bool IsProvisional { get; set; } = true;

    /// <summary>
    /// MusicBrainz Work ID (36-char UUID), or null if this piece has not been
    /// linked to a MusicBrainz work entry. Only top-level pieces carry an MBID;
    /// subpieces and versions don't have separate MB work identifiers.
    /// </summary>
    public string? MusicBrainzWorkId { get; set; }

    // ── Heterogeneous / free-shape fields stored as JSON text ────────────────
    public string? InstrumentationJson { get; set; }
    public string? CompositionYearsJson { get; set; }
    public string? TextAuthorJson { get; set; }
    public string? RolesJson { get; set; }
    public string? ArrangementsJson { get; set; }
    public string? CadenzaJson { get; set; }
    public string? TitleNumberJson { get; set; }

    // ── Denormalised sort helpers (derived from first CatalogEntry at save) ──
    public string CatalogSortPrefix { get; set; } = "\uFFFF";
    public int CatalogSortNumber { get; set; } = int.MaxValue;
    public string CatalogSortSuffix { get; set; } = "";

    // ── Navigation ───────────────────────────────────────────────────────────
    public List<PieceRow> Subpieces { get; set; } = [];
    public List<PieceVersionRow> Versions { get; set; } = [];
    public List<PieceCatalogEntryRow> CatalogEntries { get; set; } = [];
    public List<PieceMarkerRow> Markers { get; set; } = [];
    public List<PieceComposerCreditRow> ComposerCredits { get; set; } = [];
    public List<PieceVariantRow> Variants { get; set; } = [];
    public List<AlbumTrackPieceRefRow> AlbumRefs { get; set; } = [];
}
