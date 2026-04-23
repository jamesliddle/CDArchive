namespace CDArchive.Core.Models;

/// <summary>
/// One occurrence of a <see cref="TrackPieceRef"/> in the album catalogue:
/// a specific track on a specific disc of a specific album references the piece.
/// </summary>
/// <remarks>
/// Built by <see cref="Services.PieceReferenceIndex"/>; consumed by the "Show Albums…"
/// context-menu action and the hit-count badges in <c>CanonView</c>.
/// </remarks>
public record PieceAlbumHit(
    CanonAlbum Album,
    AlbumDisc Disc,
    AlbumTrack Track,
    TrackPieceRef Ref);
