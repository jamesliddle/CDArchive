namespace CDArchive.Core.Models;

public class TrackInfo
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public AudioFormat Format { get; set; }
}
