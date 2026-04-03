namespace CDArchive.Core.Models;

public class AlbumInfo
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public int DiscCount { get; set; }
    public List<DiscInfo> Discs { get; set; } = new();
    public List<string> ArtFiles { get; set; } = new();
}
