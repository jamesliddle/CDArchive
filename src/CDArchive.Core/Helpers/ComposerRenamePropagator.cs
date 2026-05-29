using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// When a composer is renamed in the editor, the SQLite save flushes the
/// row's new name immediately — but every in-memory <see cref="CanonPiece"/>
/// and <see cref="TrackPieceRef"/> still holds the OLD name in its
/// <c>Composer</c> string field. The UI groups pieces under a composer via
/// <c>string.Equals(piece.Composer, composer.Name)</c>, so the renamed
/// composer suddenly displays as having no pieces — until the next app
/// restart loads everything fresh from SQLite (where the FK relationship
/// reconstructs the new name).
///
/// <para>This helper walks every in-memory consumer of the composer-name
/// string and updates the references so the UI grouping stays consistent
/// without a reload. The DB doesn't need any of this — the SQLite save path
/// reconciles composer/piece relationships via FK (<c>pieces.composer_id</c>),
/// not by string.</para>
/// </summary>
public static class ComposerRenamePropagator
{
    /// <summary>
    /// Update every in-memory reference to <paramref name="oldName"/> to use
    /// <paramref name="newName"/>. Returns the count of fields actually
    /// changed (0 when the names compare equal). Matches are
    /// case-insensitive (the same comparison the UI grouping uses).
    /// <list type="bullet">
    ///   <item><see cref="CanonPiece.Composer"/> on every top-level piece and
    ///     recursively on its subpieces and version subpieces.</item>
    ///   <item><see cref="ComposerCredit.Name"/> on every contributor list
    ///     (pieces + versions, recursively).</item>
    ///   <item><see cref="TrackPieceRef.Composer"/> on every album-bound and
    ///     loose track's piece refs (when the album/loose collections are
    ///     supplied).</item>
    /// </list>
    /// </summary>
    public static int Propagate(
        string? oldName,
        string? newName,
        IEnumerable<CanonPiece>? pieces,
        IEnumerable<CanonAlbum>? albums = null,
        IEnumerable<AlbumTrack>? looseTracks = null)
    {
        if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName)) return 0;
        if (string.Equals(oldName, newName, StringComparison.Ordinal))     return 0;

        int updated = 0;

        if (pieces is not null)
            foreach (var p in pieces)
                updated += UpdatePieceTree(p, oldName, newName);

        if (albums is not null)
            foreach (var album in albums)
                foreach (var disc in album.Discs)
                    foreach (var track in disc.Tracks)
                        updated += UpdateTrack(track, oldName, newName);

        if (looseTracks is not null)
            foreach (var track in looseTracks)
                updated += UpdateTrack(track, oldName, newName);

        return updated;
    }

    private static int UpdatePieceTree(CanonPiece piece, string oldName, string newName)
    {
        int updated = 0;

        if (string.Equals(piece.Composer, oldName, StringComparison.OrdinalIgnoreCase))
        {
            piece.Composer = newName;
            updated++;
        }

        updated += UpdateContributorCredits(piece.Composers, oldName, newName);

        if (piece.Subpieces is { Count: > 0 })
            foreach (var sub in piece.Subpieces)
                updated += UpdatePieceTree(sub, oldName, newName);

        if (piece.Versions is { Count: > 0 })
            foreach (var ver in piece.Versions)
            {
                updated += UpdateContributorCredits(ver.Composers, oldName, newName);
                if (ver.Subpieces is { Count: > 0 })
                    foreach (var sub in ver.Subpieces)
                        updated += UpdatePieceTree(sub, oldName, newName);
            }

        return updated;
    }

    private static int UpdateContributorCredits(
        List<ComposerCredit>? credits, string oldName, string newName)
    {
        if (credits is null) return 0;
        int updated = 0;
        foreach (var c in credits)
            if (string.Equals(c.Name, oldName, StringComparison.OrdinalIgnoreCase))
            {
                c.Name = newName;
                updated++;
            }
        return updated;
    }

    private static int UpdateTrack(AlbumTrack track, string oldName, string newName)
    {
        if (track.PieceRefs is not { Count: > 0 }) return 0;
        int updated = 0;
        foreach (var pref in track.PieceRefs)
            if (string.Equals(pref.Composer, oldName, StringComparison.OrdinalIgnoreCase))
            {
                pref.Composer = newName;
                updated++;
            }
        return updated;
    }
}
