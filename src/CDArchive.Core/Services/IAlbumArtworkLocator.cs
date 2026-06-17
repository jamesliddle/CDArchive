using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Resolves a <see cref="CanonAlbum"/> to its cover art. Resolution order:
///   1. Embedded picture in the album's first (or the given) track's audio
///      file — art that travels with the rip is the most reliable source.
///   2. A conventionally-named image (cover / folder / front / album, in
///      jpg/jpeg/png/bmp) in the album's archive folder.
///   3. The single image file in that folder (or the largest of several),
///      since real archives often store one cover scan under an arbitrary
///      download id rather than "cover.jpg".
/// For a multi-disc album a disc-specific folder is preferred for the file
/// fallbacks. The archive folder is the same one
/// <see cref="IArchiveAudioLocator"/> resolves audio from.
/// </summary>
public interface IAlbumArtworkLocator
{
    /// <summary>
    /// Returns the resolved <see cref="AlbumArtwork"/> for the album, or null
    /// when no art can be found. <paramref name="disc"/> / <paramref name="track"/>
    /// (when supplied, e.g. by the player) target embedded extraction + the
    /// disc-folder image at the currently-playing track; otherwise the album's
    /// first track / album-root image is used.
    /// </summary>
    AlbumArtwork? Resolve(CanonAlbum album, AlbumDisc? disc = null, AlbumTrack? track = null);

    /// <summary>
    /// Resolves art for a loose track (one with no owning album) from the
    /// <em>embedded picture in the track's own audio file</em> only — its
    /// <see cref="AlbumTrack.FlacPath"/> / <see cref="AlbumTrack.Mp3Path"/>
    /// override (preferred format first). No folder-image fallback: a loose
    /// track's folder may hold many unrelated files, so a folder image can't be
    /// attributed to it unambiguously. Returns null when there's no override
    /// file or no embedded picture.
    /// </summary>
    AlbumArtwork? ResolveFromTrack(AlbumTrack track);

    /// <summary>
    /// Clears cached filesystem probes + embedded-picture reads. Call when
    /// artwork may have changed on disk outside the app, or an album's archive
    /// folder was renamed. Mirrors <see cref="IArchiveAudioLocator.Invalidate"/>.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// A resolved piece of album art: either a standalone image <see cref="FilePath"/>
/// on disk, or <see cref="EmbeddedData"/> bytes extracted from an audio file's
/// tags. <see cref="CacheKey"/> is a stable identity for the App-side image
/// cache (the file path, or the source audio path with an "#embedded" suffix).
/// </summary>
public sealed class AlbumArtwork
{
    public string CacheKey { get; }
    public string? FilePath { get; }
    public byte[]? EmbeddedData { get; }

    private AlbumArtwork(string cacheKey, string? filePath, byte[]? embeddedData)
    {
        CacheKey = cacheKey;
        FilePath = filePath;
        EmbeddedData = embeddedData;
    }

    public static AlbumArtwork FromFile(string path) => new(path, path, null);

    public static AlbumArtwork FromEmbedded(string sourceAudioPath, byte[] data) =>
        new(sourceAudioPath + "#embedded", null, data);
}
