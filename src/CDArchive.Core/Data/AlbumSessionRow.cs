namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_sessions</c>. One recording session. Tracks
/// reference a session via <see cref="AlbumTrackRow.SessionId"/>.
/// Engineer/producer name lists are stored as JSON arrays of strings to
/// avoid a two-deep sub-table for these rarely-queried credits.
/// </summary>
public class AlbumSessionRow
{
    public long Id { get; set; }

    public long AlbumId { get; set; }
    public AlbumRow Album { get; set; } = null!;

    public int Position { get; set; }

    public string? Dates { get; set; }
    public string? Venue { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Country { get; set; }

    /// <summary>JSON array of engineer name strings; null when none.</summary>
    public string? EngineersJson { get; set; }

    /// <summary>JSON array of producer name strings; null when none.</summary>
    public string? ProducersJson { get; set; }

    public List<AlbumTrackRow> Tracks { get; set; } = [];
}
