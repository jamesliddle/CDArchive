namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_performers</c>. Represents one credit (person or
/// ensemble) on either the album (<see cref="TrackId"/> null) or a specific
/// track (<see cref="TrackId"/> set).
/// <list type="bullet">
///   <item>At most one of <see cref="PersonId"/> / <see cref="EnsembleId"/> is set.</item>
///   <item>At least one of <see cref="PersonId"/> / <see cref="EnsembleId"/> / <see cref="DisplayName"/> is set.</item>
/// </list>
/// <see cref="DisplayName"/> is used as the raw credit text on migration and
/// as an explicit override when the linked entity's canonical name shouldn't be used.
/// </summary>
public class AlbumPerformerRow
{
    public long Id { get; set; }

    public long AlbumId { get; set; }
    public AlbumRow Album { get; set; } = null!;

    /// <summary>NULL = album-level credit. Set = per-track override.</summary>
    public long? TrackId { get; set; }
    public AlbumTrackRow? Track { get; set; }

    public int Position { get; set; }

    public long? PersonId { get; set; }
    public PersonRow? Person { get; set; }

    public long? EnsembleId { get; set; }
    public EnsembleRow? Ensemble { get; set; }

    public string? DisplayName { get; set; }

    public string? Role { get; set; }
    public string? Instrument { get; set; }
}
