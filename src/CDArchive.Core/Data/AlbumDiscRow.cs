namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_discs</c>. One physical disc; disc numbers restart
/// at 1 within each volume (or within the album if there are no volumes).
/// </summary>
public class AlbumDiscRow
{
    public long Id { get; set; }

    public long AlbumId { get; set; }
    public AlbumRow Album { get; set; } = null!;

    public long? VolumeId { get; set; }
    public AlbumVolumeRow? Volume { get; set; }

    public int DiscNumber { get; set; }
    public string? Title { get; set; }

    public List<AlbumTrackRow> Tracks { get; set; } = [];
}
