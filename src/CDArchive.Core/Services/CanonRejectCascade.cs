using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Cascade-aware delete logic for the canon's Reject Composer / Reject Piece
/// flows. Extracted from <c>CanonViewModel</c> so the side-effect chain (mutate
/// in-memory lists + save in FK-dependency order) is unit-testable without WPF
/// in the loop. The UI layer is responsible for confirmation dialogs and error
/// surfacing; this class just performs the mutation + persist.
///
/// <para>The FK landscape that drives the ordering:</para>
/// <list type="bullet">
///   <item><c>album_track_piece_refs.piece_id → pieces.id</c> is
///     <c>OnDelete: Restrict</c> — refs must be stripped before pieces.</item>
///   <item><c>pieces.composer_id → composers.id</c> is
///     <c>OnDelete: Restrict</c> — pieces must be deleted before composer.</item>
///   <item><c>piece_composer_credits.composer_id → composers.id</c> is
///     <c>OnDelete: Restrict</c> — contributor credits naming the composer
///     must be scrubbed from surviving pieces.</item>
/// </list>
/// </summary>
public static class CanonRejectCascade
{
    public record RejectResult(int PiecesDeleted, int RefsStripped, int CreditsStripped);

    /// <summary>
    /// Removes <paramref name="rejected"/> from <paramref name="composers"/>,
    /// removes every piece in <paramref name="pieces"/> whose
    /// <see cref="CanonPiece.Composer"/> matches the composer's name, strips
    /// album track refs pointing at those pieces, scrubs contributor credits
    /// naming the composer on surviving pieces, then saves through
    /// <paramref name="svc"/> in the order required by the FK restrict chain:
    /// <c>SaveAlbumsAsync → SavePiecesAsync → SaveComposersAsync</c>.
    /// </summary>
    public static Task<RejectResult> RejectComposerAsync(
        ICanonDataService svc,
        IList<CanonComposer> composers,
        IList<CanonPiece>    pieces,
        CanonComposer        rejected)
        => RejectComposerAsync(svc, composers, pieces, albums: null, looseTracks: null, rejected);

    /// <summary>
    /// In-place overload: when <paramref name="albums"/> and
    /// <paramref name="looseTracks"/> are non-null, the cascade mutates the
    /// caller's lists rather than loading fresh copies internally. The caller
    /// (typically <c>CanonViewModel</c>) can then reuse those lists for a
    /// post-save index rebuild without a second DB load. See Rework H9.
    /// Null preserves the legacy load-fresh behaviour for callers that don't
    /// hold the container state.
    /// </summary>
    public static async Task<RejectResult> RejectComposerAsync(
        ICanonDataService svc,
        IList<CanonComposer> composers,
        IList<CanonPiece>    pieces,
        List<CanonAlbum>?    albums,
        List<AlbumTrack>?    looseTracks,
        CanonComposer        rejected)
    {
        var name = rejected.Name;

        var piecesToDelete = pieces
            .Where(p => string.Equals(p.Composer, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var doomedKeys = new HashSet<(string composer, string title)>(
            piecesToDelete.Select(p => (
                (p.Composer ?? "").ToLowerInvariant(),
                (p.Title    ?? "").ToLowerInvariant())));

        albums      ??= await svc.LoadAlbumsAsync().ConfigureAwait(false);
        looseTracks ??= await svc.LoadLooseTracksAsync().ConfigureAwait(false);

        // Strip refs from both kinds of container — album-bound tracks and
        // loose tracks both have piece_id FKs that would otherwise block the
        // piece-row delete.
        var strippedFromAlbums = StripPieceRefs(albums, doomedKeys);
        var strippedFromLoose  = StripPieceRefsFromTracks(looseTracks, doomedKeys);
        var strippedRefs       = strippedFromAlbums + strippedFromLoose;
        var strippedCredits    = StripContributorCredits(pieces, name, piecesToDelete);

        foreach (var p in piecesToDelete) pieces.Remove(p);
        composers.Remove(rejected);

        // Dependency order: strip refs → delete pieces → delete composer. Each
        // save's orphan-delete pass on the prior level frees the FK for the next.
        if (strippedFromAlbums > 0)
            await svc.SaveAlbumsAsync(albums).ConfigureAwait(false);
        if (strippedFromLoose > 0)
            await svc.SaveLooseTracksAsync(looseTracks).ConfigureAwait(false);
        if (piecesToDelete.Count > 0 || strippedCredits > 0)
            await svc.SavePiecesAsync(pieces.ToList()).ConfigureAwait(false);
        await svc.SaveComposersAsync(composers.ToList()).ConfigureAwait(false);

        return new RejectResult(piecesToDelete.Count, strippedRefs, strippedCredits);
    }

    /// <summary>
    /// Removes <paramref name="rejected"/> from <paramref name="pieces"/>,
    /// strips album track refs pointing at it, and saves albums then pieces.
    /// </summary>
    public static Task<RejectResult> RejectPieceAsync(
        ICanonDataService svc,
        IList<CanonPiece> pieces,
        CanonPiece        rejected)
        => RejectPieceAsync(svc, pieces, albums: null, looseTracks: null, rejected);

    /// <summary>
    /// In-place overload — see the overload comment on
    /// <see cref="RejectComposerAsync"/>. Same Rework H9 motivation.
    /// </summary>
    public static async Task<RejectResult> RejectPieceAsync(
        ICanonDataService svc,
        IList<CanonPiece> pieces,
        List<CanonAlbum>? albums,
        List<AlbumTrack>? looseTracks,
        CanonPiece        rejected)
    {
        var doomedKey = new HashSet<(string composer, string title)>
        {
            ((rejected.Composer ?? "").ToLowerInvariant(),
             (rejected.Title    ?? "").ToLowerInvariant()),
        };

        albums      ??= await svc.LoadAlbumsAsync().ConfigureAwait(false);
        looseTracks ??= await svc.LoadLooseTracksAsync().ConfigureAwait(false);

        var strippedFromAlbums = StripPieceRefs(albums, doomedKey);
        var strippedFromLoose  = StripPieceRefsFromTracks(looseTracks, doomedKey);
        var strippedRefs       = strippedFromAlbums + strippedFromLoose;

        pieces.Remove(rejected);

        if (strippedFromAlbums > 0)
            await svc.SaveAlbumsAsync(albums).ConfigureAwait(false);
        if (strippedFromLoose > 0)
            await svc.SaveLooseTracksAsync(looseTracks).ConfigureAwait(false);
        await svc.SavePiecesAsync(pieces.ToList()).ConfigureAwait(false);

        return new RejectResult(PiecesDeleted: 1, strippedRefs, CreditsStripped: 0);
    }

    /// <summary>
    /// Drops every <see cref="TrackPieceRef"/> whose (composer, piece-title)
    /// pair matches one of <paramref name="doomedKeys"/> (both lowercased).
    /// Tracks whose entire ref list is stripped become uncatalogued
    /// (<see cref="AlbumTrack.PieceRefs"/> = null).
    /// </summary>
    public static int StripPieceRefs(
        IEnumerable<CanonAlbum> albums,
        HashSet<(string composer, string title)> doomedKeys)
    {
        int stripped = 0;
        foreach (var album in albums)
        foreach (var disc in album.Discs)
            stripped += StripPieceRefsFromTracks(disc.Tracks, doomedKeys);
        return stripped;
    }

    /// <summary>
    /// Same as <see cref="StripPieceRefs"/> but for a flat track list — used to
    /// scrub loose tracks (which have no album/disc parent to walk through).
    /// </summary>
    public static int StripPieceRefsFromTracks(
        IEnumerable<AlbumTrack> tracks,
        HashSet<(string composer, string title)> doomedKeys)
    {
        int stripped = 0;
        foreach (var track in tracks)
        {
            if (track.PieceRefs is not { Count: > 0 } refs) continue;
            var kept = refs.Where(r =>
                !doomedKeys.Contains((
                    (r.Composer   ?? "").ToLowerInvariant(),
                    (r.PieceTitle ?? "").ToLowerInvariant()))).ToList();
            if (kept.Count == refs.Count) continue;
            stripped += refs.Count - kept.Count;
            track.PieceRefs = kept.Count > 0 ? kept : null;
        }
        return stripped;
    }

    /// <summary>
    /// Removes contributor-credit entries naming <paramref name="composerName"/>
    /// from every piece in <paramref name="pieces"/> that is NOT itself being
    /// deleted (<paramref name="piecesBeingDeleted"/>).
    /// </summary>
    public static int StripContributorCredits(
        IEnumerable<CanonPiece>  pieces,
        string                   composerName,
        IReadOnlyList<CanonPiece> piecesBeingDeleted)
    {
        int stripped = 0;
        foreach (var piece in pieces)
        {
            if (piecesBeingDeleted.Contains(piece)) continue;
            if (piece.Composers is not { Count: > 0 } credits) continue;
            var kept = credits.Where(c =>
                !string.Equals(c.Name, composerName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (kept.Count == credits.Count) continue;
            stripped += credits.Count - kept.Count;
            piece.Composers = kept.Count > 0 ? kept : null;
        }
        return stripped;
    }
}
