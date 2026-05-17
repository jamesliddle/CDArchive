namespace CDArchive.Core.Models;

/// <summary>
/// One occurrence of a <see cref="TrackPieceRef"/> in the catalogue: a specific
/// track references the piece. Album-bound tracks set both <see cref="Album"/>
/// and <see cref="Disc"/>; loose tracks leave both null and identify the
/// container by <see cref="Track"/> alone.
/// </summary>
/// <remarks>
/// Built by <see cref="Services.PieceReferenceIndex"/>; consumed by the "Show Albums…"
/// context-menu action and the hit-count badges in <c>CanonView</c>.
/// </remarks>
public record PieceAlbumHit(
    CanonAlbum? Album,
    AlbumDisc? Disc,
    AlbumTrack Track,
    TrackPieceRef Ref)
{
    /// <summary>True when this hit comes from a loose track (no owning album).</summary>
    public bool IsLooseTrack => Album is null;
}
