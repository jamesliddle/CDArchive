using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// In-memory approve / reject mutations for tracks, extracted from
/// <c>TracksViewModel</c> so the side-effect chain is unit-testable without
/// WPF. The view-model layer wraps these to combine them with saves through
/// the data service and to surface confirmation dialogs.
/// </summary>
public static class TrackCascade
{
    /// <summary>
    /// Clears <see cref="AlbumTrack.IsProvisional"/> on every track in
    /// <paramref name="tracks"/>. Returns the number of tracks whose value
    /// actually changed (already-approved tracks aren't counted).
    /// </summary>
    public static int Approve(IEnumerable<AlbumTrack> tracks)
    {
        int changed = 0;
        foreach (var t in tracks)
        {
            if (t.IsProvisional)
            {
                t.IsProvisional = false;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Result of <see cref="Reject"/>: counts of how many album-bound and loose
    /// tracks were actually removed (mismatches between input and storage
    /// don't crash; they just don't contribute to the counts). The caller uses
    /// these to decide which save methods to invoke.
    /// </summary>
    public record RejectResult(int AlbumBoundRemoved, int LooseRemoved)
    {
        public int Total => AlbumBoundRemoved + LooseRemoved;
    }

    /// <summary>
    /// Removes each entry's track from its owner. Album-bound entries (Disc
    /// non-null) lose their track from <c>disc.Tracks</c>; loose entries
    /// (Disc null) lose their track from <paramref name="looseTracks"/>. The
    /// subsequent save through the data service does the actual DB delete
    /// via its orphan-delete pass.
    /// </summary>
    public static RejectResult Reject(
        IEnumerable<(AlbumTrack Track, AlbumDisc? Disc)> entries,
        IList<AlbumTrack> looseTracks)
    {
        int albumBound = 0, loose = 0;
        foreach (var (track, disc) in entries)
        {
            if (disc is not null)
            {
                if (disc.Tracks.Remove(track)) albumBound++;
            }
            else
            {
                if (looseTracks.Remove(track)) loose++;
            }
        }
        return new RejectResult(albumBound, loose);
    }
}
