using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Finds the album / loose-track recordings that identify a given variant, as
/// <see cref="PieceAlbumHit"/> rows. Drives the piece editor's
/// "Recordings using this variant…" view so the user can locate (and clear)
/// the selections that block a variant delete.
/// </summary>
public static class VariantUsageFinder
{
    /// <summary>
    /// Scans <paramref name="albums"/> and <paramref name="looseTracks"/> for
    /// every track-piece ref that identifies the variant with
    /// <paramref name="variantId"/> (matched on
    /// <see cref="VariantReference.Id"/>), returning one hit per matching ref.
    /// <see cref="PieceAlbumHit.Album"/> / <see cref="PieceAlbumHit.Disc"/> are
    /// null for loose tracks. Returns empty for a zero id (a freshly-added,
    /// never-saved variant can't be referenced).
    /// </summary>
    public static List<PieceAlbumHit> Find(
        IEnumerable<CanonAlbum> albums,
        IEnumerable<AlbumTrack> looseTracks,
        long variantId)
    {
        var hits = new List<PieceAlbumHit>();
        if (variantId == 0) return hits;

        foreach (var album in albums)
            foreach (var disc in album.Discs)
                foreach (var track in disc.Tracks)
                    foreach (var r in MatchingRefs(track, variantId))
                        hits.Add(new PieceAlbumHit(album, disc, track, r));

        foreach (var track in looseTracks)
            foreach (var r in MatchingRefs(track, variantId))
                hits.Add(new PieceAlbumHit(null, null, track, r));

        return hits;
    }

    private static IEnumerable<TrackPieceRef> MatchingRefs(AlbumTrack track, long variantId)
    {
        if (track.PieceRefs is not { Count: > 0 }) yield break;
        foreach (var r in track.PieceRefs)
            if (r.Variants is { Count: > 0 } && r.Variants.Any(v => v.Id == variantId))
                yield return r;
    }
}
