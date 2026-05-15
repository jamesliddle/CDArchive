using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Resolves an <see cref="AlbumTrack"/> on a <see cref="CanonAlbum"/> to the
/// playable audio file on disk. The locator combines:
///   • Per-track override paths (<see cref="AlbumTrack.FlacPath"/> /
///     <see cref="AlbumTrack.Mp3Path"/>) — used as-is when set.
///   • A convention-based search rooted at
///     <see cref="IArchiveSettings.ArchiveRootPath"/>.
/// </summary>
public interface IArchiveAudioLocator
{
    /// <summary>
    /// Returns the absolute path to the audio file for <paramref name="track"/>,
    /// or null if no file can be found. The format chosen follows
    /// <see cref="IArchiveSettings.PreferredAudioFormat"/> with fall-back to the
    /// other format when the preferred one is missing.
    /// </summary>
    AudioFileLocation? Resolve(CanonAlbum album, AlbumDisc disc, AlbumTrack track);
}

/// <summary>
/// Result of a successful audio-file resolution: the absolute path plus the
/// concrete format that was found (which may differ from the user's preferred
/// format if the preferred one was missing).
/// </summary>
public readonly record struct AudioFileLocation(string Path, AudioFormat Format);
