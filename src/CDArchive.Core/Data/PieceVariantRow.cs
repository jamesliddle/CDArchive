namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>piece_variants</c>. Corresponds to <c>VariantInfo</c>.
/// Exactly one of <see cref="PieceId"/> / <see cref="VersionId"/> is non-null.
/// </summary>
public class PieceVariantRow
{
    public long Id { get; set; }

    public long? PieceId { get; set; }
    public PieceRow? Piece { get; set; }

    public long? VersionId { get; set; }
    public PieceVersionRow? Version { get; set; }

    public int Position { get; set; }

    public string Description { get; set; } = "";
    public string? LongDescription { get; set; }
}
