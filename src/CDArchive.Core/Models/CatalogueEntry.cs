namespace CDArchive.Core.Models;

public class CatalogueEntry
{
    public string FilePath { get; set; } = string.Empty;
    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public int? DiscCount { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Composer { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string SortName { get; set; } = string.Empty;
    public string SortAlbum { get; set; } = string.Empty;
    public string SortArtist { get; set; } = string.Empty;
    public string SortComposer { get; set; } = string.Empty;
}
