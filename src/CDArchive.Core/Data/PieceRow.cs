using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CDArchive.Core.Data;

/// <summary>
/// EF Core entity for a top-level Canon piece.
/// Scalar columns hold indexed/sort fields; all complex/nested data is stored as JSON blobs.
/// </summary>
[Table("Pieces")]
public class PieceRow
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    // ── Indexed scalar columns ───────────────────────────────────────────────

    public string? Composer { get; set; }
    public string? Form { get; set; }
    public string? Title { get; set; }
    public string? TitleEnglish { get; set; }
    public string? Nickname { get; set; }
    public string? Subtitle { get; set; }
    public int? Number { get; set; }
    public string? KeyTonality { get; set; }
    public string? KeyMode { get; set; }
    public string? InstrumentationCategory { get; set; }
    public int? PublicationYear { get; set; }
    public bool? NumberedSubpieces { get; set; }
    public string? MusicNumber { get; set; }
    public string? FirstLine { get; set; }

    // ── Sort-helper columns (derived from CatalogInfo at save time) ──────────

    /// <summary>Catalog prefix, e.g. "Op." — used for ORDER BY.</summary>
    public string? CatalogSortPrefix { get; set; }

    /// <summary>Numeric catalog number — used for ORDER BY.</summary>
    public int? CatalogSortNumber { get; set; }

    /// <summary>Trailing alphabetic suffix after the catalog number, e.g. "a".</summary>
    public string? CatalogSortSuffix { get; set; }

    // ── JSON blob columns ────────────────────────────────────────────────────

    public string? CatalogInfoJson { get; set; }
    public string? InstrumentationJson { get; set; }
    public string? CompositionYearsJson { get; set; }
    public string? TextAuthorJson { get; set; }
    public string? ArrangementsJson { get; set; }
    public string? RolesJson { get; set; }
    public string? CadenzaJson { get; set; }
    public string? TitleNumberJson { get; set; }
    public string? TemposJson { get; set; }
    public string? SubpiecesJson { get; set; }
    public string? VersionsJson { get; set; }
}
