namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_track_piece_refs</c>. Replaces the path-based
/// <c>TrackPieceRef</c> with a direct FK to either a top-level piece or
/// a subpiece (both live in the <c>pieces</c> table).
/// </summary>
public class AlbumTrackPieceRefRow
{
    public long Id { get; set; }

    public long TrackId { get; set; }
    public AlbumTrackRow Track { get; set; } = null!;

    public int Position { get; set; }

    /// <summary>FK to <c>pieces.Id</c>. May be a top-level piece or a subpiece.</summary>
    public long PieceId { get; set; }
    public PieceRow Piece { get; set; } = null!;

    /// <summary>Optional link to a specific version of the piece.</summary>
    public long? VersionId { get; set; }
    public PieceVersionRow? Version { get; set; }

    /// <summary>
    /// Optional end-piece for range-spanning refs (a single track that covers
    /// several adjacent sibling subpieces). When non-null, must be a sibling
    /// of <see cref="PieceId"/> under the same parent and at the same depth.
    /// Resolution credits every leaf subpiece in <c>[PieceId..EndPieceId]</c>
    /// inclusive.
    /// </summary>
    public long? EndPieceId { get; set; }
    public PieceRow? EndPiece { get; set; }

    /// <summary>Optional marker pinning the start of the track inside <see cref="Piece"/>.</summary>
    public long? StartMarkerId { get; set; }
    public PieceMarkerRow? StartMarker { get; set; }

    /// <summary>
    /// Optional marker pinning the end of the track. Lives in
    /// <see cref="EndPiece"/> when <see cref="EndPieceId"/> is set, otherwise
    /// in <see cref="Piece"/>.
    /// </summary>
    public long? EndMarkerId { get; set; }
    public PieceMarkerRow? EndMarker { get; set; }

    /// <summary>Optional display override when the CD's title differs from the canonical title.</summary>
    public string? DisplayLabel { get; set; }
}
