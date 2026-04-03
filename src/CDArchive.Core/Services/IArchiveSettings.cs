namespace CDArchive.Core.Services;

public interface IArchiveSettings
{
    string ArchiveRootPath { get; set; }
    string FfmpegPath { get; set; }
    int Mp3Bitrate { get; set; }
    void Save();
    void Load();
}
