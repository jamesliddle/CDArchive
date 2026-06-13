namespace CDArchive.Core.Data;

/// <summary>
/// Join row for <c>album_track_piece_ref_variants</c>: links an
/// <see cref="AlbumTrackPieceRefRow"/> to a <see cref="PieceVariantRow"/>,
/// recording which variant(s) a recording uses. Positional within the ref.
/// <para>
/// <see cref="RefId"/> cascades (the join rows die with their ref);
/// <see cref="VariantId"/> is Restrict so a variant that a recording still
/// identifies can't be deleted out from under it — the piece-save path
/// surfaces a friendly diagnostic instead of a raw FK failure.
/// </para>
/// </summary>
public class AlbumTrackPieceRefVariantRow
{
    public long Id { get; set; }

    public long RefId { get; set; }
    public AlbumTrackPieceRefRow Ref { get; set; } = null!;

    public long VariantId { get; set; }
    public PieceVariantRow Variant { get; set; } = null!;

    public int Position { get; set; }
}
