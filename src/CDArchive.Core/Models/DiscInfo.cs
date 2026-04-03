namespace CDArchive.Core.Models;

public class DiscInfo
{
    public int DiscNumber { get; set; }
    public string FolderName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool HasFlacFolder { get; set; }
    public bool HasMp3Folder { get; set; }
    public List<TrackInfo> FlacTracks { get; set; } = new();
    public List<TrackInfo> Mp3Tracks { get; set; } = new();
}
