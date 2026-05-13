namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>albums</c> table.
/// Identity: (<see cref="Label"/>, <see cref="CatalogueNumber"/>) unique when both set.
/// </summary>
public class AlbumRow
{
    public long Id { get; set; }

    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Label { get; set; }
    public string? CatalogueNumber { get; set; }
    public string? Barcode { get; set; }
    public string? SparsCode { get; set; }
    public bool? IsStereo { get; set; }
    public string? Notes { get; set; }

    /// <summary>True until explicitly approved. Defaults true for new albums.</summary>
    public bool IsProvisional { get; set; } = true;

    public List<AlbumVolumeRow> Volumes { get; set; } = [];
    public List<AlbumDiscRow> Discs { get; set; } = [];
    public List<AlbumPerformerRow> Performers { get; set; } = [];
    public List<AlbumSessionRow> Sessions { get; set; } = [];
}
