namespace CDArchive.Core.Models;

/// <summary>
/// A single track read from the iTunes Music Library XML, with the fields needed
/// by the iTunes Import workflow. This is a pure DTO — no business logic, no
/// link into the Canon. The inference engine consumes a list of these and
/// produces <see cref="CanonAlbum"/> + <see cref="TrackPieceRef"/> graphs.
/// </summary>
/// <param name="TrackId">iTunes internal track id (key in the Tracks dict).</param>
/// <param name="PersistentId">iTunes "Persistent ID" — stable across DB rebuilds.</param>
/// <param name="DiscNumber">Disc within the album, or null if not set.</param>
/// <param name="TrackNumber">Track within the disc, or null if not set.</param>
/// <param name="Name">Track name — the hierarchical "Piece - Subpiece - …" field.</param>
/// <param name="DurationMs">Track duration in milliseconds.</param>
/// <param name="Genre">Genre tag.</param>
/// <param name="Composer">Composer field, typically "LastName, FirstName (YYYY–YYYY)".</param>
/// <param name="Album">Album name (iTunes album entity).</param>
/// <param name="AlbumArtist">Album-level artist (often the ensemble or conductor).</param>
/// <param name="Artist">Track-level artist.</param>
/// <param name="DateAdded">When the track was added to iTunes.</param>
/// <param name="Location">File URI ("file://…") or empty for streaming-only tracks.</param>
/// <param name="MbReleaseId">Optional MusicBrainz Release ID parsed from the iTunes Comments
///   field (populated by MusicBrainz Picard or similar). When present, the import
///   planner skips the MB release-search and fetches the release by MBID directly.</param>
/// <param name="MbRecordingId">Optional MusicBrainz Recording ID parsed from the iTunes Comments.</param>
/// <param name="MbWorkId">Optional MusicBrainz Work ID parsed from the iTunes Comments.</param>
public record ItunesTrack(
    int TrackId,
    string? PersistentId,
    int? DiscNumber,
    int? TrackNumber,
    string Name,
    int? DurationMs,
    string? Genre,
    string? Composer,
    string? Album,
    string? AlbumArtist,
    string? Artist,
    DateTime? DateAdded,
    string? Location,
    string? MbReleaseId = null,
    string? MbRecordingId = null,
    string? MbWorkId = null)
{
    /// <summary>"m:ss" or "h:mm:ss" display, or empty when duration is unknown.</summary>
    public string DurationDisplay
    {
        get
        {
            if (DurationMs is not int ms) return "";
            var d = TimeSpan.FromMilliseconds(ms);
            return d.TotalHours >= 1
                ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
                : $"{d.Minutes}:{d.Seconds:D2}";
        }
    }
}
