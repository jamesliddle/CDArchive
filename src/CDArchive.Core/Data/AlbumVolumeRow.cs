namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>album_volumes</c>. Optional grouping between a box set
/// and its individual discs, e.g. "Volume 3: Wind Music".
/// </summary>
public class AlbumVolumeRow
{
    public long Id { get; set; }

    public long AlbumId { get; set; }
    public AlbumRow Album { get; set; } = null!;

    public int Number { get; set; }
    public string? Title { get; set; }
    public string? Subtitle { get; set; }

    public List<AlbumDiscRow> Discs { get; set; } = [];
}
