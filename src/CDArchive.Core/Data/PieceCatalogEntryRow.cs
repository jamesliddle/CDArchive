namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>piece_catalog_entries</c>. A single catalogue reference
/// (Op., BWV, K., etc.) belonging to either a piece or a version.
/// Exactly one of <see cref="PieceId"/> / <see cref="VersionId"/> is non-null
/// (enforced by CHECK constraint).
/// </summary>
public class PieceCatalogEntryRow
{
    public long Id { get; set; }

    public long? PieceId { get; set; }
    public PieceRow? Piece { get; set; }

    public long? VersionId { get; set; }
    public PieceVersionRow? Version { get; set; }

    public int Position { get; set; }

    public string Catalog { get; set; } = "";
    public string? CatalogNumber { get; set; }
    public string? CatalogSubnumber { get; set; }
}
