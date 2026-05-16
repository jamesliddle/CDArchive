namespace CDArchive.Core.Services;

public interface IArchiveSettings
{
    string ArchiveRootPath { get; set; }
    string FfmpegPath { get; set; }
    int Mp3Bitrate { get; set; }

    /// <summary>
    /// Player preference for which audio format to play when both FLAC and MP3
    /// exist. The locator falls back to the other format if the preferred one
    /// is missing.
    /// </summary>
    PreferredAudioFormat PreferredAudioFormat { get; set; }

    /// <summary>
    /// Player output volume in [0.0, 1.0]. Persisted across sessions; passed
    /// through to the audio engine on every Load() so each track inherits the
    /// last-set level.
    /// </summary>
    float PlayerVolume { get; set; }

    void Save();
    void Load();
}
