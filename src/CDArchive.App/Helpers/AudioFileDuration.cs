using System.IO;
using NAudio.Wave;

namespace CDArchive.App.Helpers;

/// <summary>
/// Reads the length of an audio file via the same MediaFoundation engine the
/// player uses (handles FLAC and MP3 natively on Win10 1709+ / Win11). Used by
/// the TrackEditor to display authoritative durations whenever a per-track
/// audio file override is set — and to flag a FLAC/MP3 length mismatch before
/// the user saves a contradictory pairing.
/// </summary>
public static class AudioFileDuration
{
    /// <summary>
    /// Returns the duration of <paramref name="path"/>, or <c>null</c> when the
    /// path is empty, the file doesn't exist, or the decoder can't open it.
    /// Failures are swallowed deliberately — this is a UI helper, not a
    /// validation point.
    /// </summary>
    public static TimeSpan? Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Same format as <see cref="ViewModels.PlayerViewModel"/>'s time display:
    /// <c>m:ss</c> for under an hour, <c>h:mm:ss</c> otherwise. Matches the
    /// existing AlbumTrack.Duration string convention so user-typed and
    /// file-derived values render identically.
    /// </summary>
    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
}
