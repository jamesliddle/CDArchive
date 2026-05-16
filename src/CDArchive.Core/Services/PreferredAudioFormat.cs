namespace CDArchive.Core.Services;

/// <summary>
/// Which audio format the player prefers when both FLAC and MP3 exist for a
/// track. The locator falls back to the other format when the preferred one
/// is missing.
/// </summary>
public enum PreferredAudioFormat
{
    Flac = 0,
    Mp3  = 1,
}
