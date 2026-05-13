namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_tracks</c>. A track is catalogued (has one or more
/// <see cref="PieceRefs"/>) or uncatalogued (empty PieceRefs, uses
/// <see cref="Description"/> for display).
/// </summary>
public class AlbumTrackRow
{
    public long Id { get; set; }

    public long DiscId { get; set; }
    public AlbumDiscRow Disc { get; set; } = null!;

    public int TrackNumber { get; set; }

    /// <summary>Track duration in "m:ss" or "h:mm:ss" format.</summary>
    public string? Duration { get; set; }

    /// <summary>Freeform label when the track isn't linked to a canon piece.</summary>
    public string? Description { get; set; }

    public long? SessionId { get; set; }
    public AlbumSessionRow? Session { get; set; }

    public string? SparsCode { get; set; }

    /// <summary>True until explicitly approved. Defaults true for new tracks.</summary>
    public bool IsProvisional { get; set; } = true;

    public List<AlbumTrackPieceRefRow> PieceRefs { get; set; } = [];

    /// <summary>
    /// Track-level performer overrides. When any rows exist with this TrackId,
    /// they replace the album-level performers entirely for this track (matching
    /// the non-null-overrides-all semantics of the JSON model).
    /// </summary>
    public List<AlbumPerformerRow> Performers { get; set; } = [];
}
