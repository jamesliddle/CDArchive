using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Default <see cref="IArchiveAudioLocator"/>. Resolution order per track:
///   1. Per-track override path (preferred format first, then fallback).
///   2. Convention: <c>{ArchiveRoot}\{ArchiveFolder}[\{DiscFolder}]\{FLAC|MP3}\{NN}*.{flac|mp3}</c>
///      where <c>{NN}</c> is the zero-padded track number. The disc folder is
///      omitted on single-disc albums (and the disc has no explicit
///      <see cref="AlbumDisc.FolderName"/>).
///   3. Returns null if neither yields a file that exists.
/// </summary>
public sealed class ArchiveAudioLocator : IArchiveAudioLocator
{
    private readonly IArchiveSettings _settings;

    public ArchiveAudioLocator(IArchiveSettings settings) => _settings = settings;

    public AudioFileLocation? Resolve(CanonAlbum album, AlbumDisc disc, AlbumTrack track)
    {
        var prefer = _settings.PreferredAudioFormat;

        // 1. Per-track overrides — preferred first, then the other.
        if (prefer == PreferredAudioFormat.Flac)
        {
            if (TryOverride(track.FlacPath, AudioFormat.Flac, out var hit)) return hit;
            if (TryOverride(track.Mp3Path,  AudioFormat.Mp3,  out hit))     return hit;
        }
        else
        {
            if (TryOverride(track.Mp3Path,  AudioFormat.Mp3,  out var hit)) return hit;
            if (TryOverride(track.FlacPath, AudioFormat.Flac, out hit))     return hit;
        }

        // 2. Convention.
        var albumDir = ResolveAlbumDirectory(album);
        if (albumDir is null) return null;

        var discDir = ResolveDiscDirectory(albumDir, album, disc);
        if (discDir is null) return null;

        if (prefer == PreferredAudioFormat.Flac)
        {
            if (TryConvention(discDir, "FLAC", "flac", track.TrackNumber, AudioFormat.Flac, out var hit)) return hit;
            if (TryConvention(discDir, "MP3",  "mp3",  track.TrackNumber, AudioFormat.Mp3,  out hit))     return hit;
        }
        else
        {
            if (TryConvention(discDir, "MP3",  "mp3",  track.TrackNumber, AudioFormat.Mp3,  out var hit)) return hit;
            if (TryConvention(discDir, "FLAC", "flac", track.TrackNumber, AudioFormat.Flac, out hit))     return hit;
        }

        return null;
    }

    private static bool TryOverride(string? path, AudioFormat format, out AudioFileLocation hit)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            hit = new AudioFileLocation(path, format);
            return true;
        }
        hit = default;
        return false;
    }

    /// <summary>
    /// Returns the absolute folder for the album, applying the absolute-vs-relative
    /// rule (rooted path → as-is; otherwise → resolved under ArchiveRootPath).
    /// Falls back to <see cref="CanonAlbum.Title"/> as a relative folder name
    /// when <see cref="CanonAlbum.ArchiveFolder"/> is null — i.e. the convention
    /// "the album's archive folder is named the same as its title" is the
    /// default, and ArchiveFolder is the explicit override.
    /// </summary>
    private string? ResolveAlbumDirectory(CanonAlbum album)
    {
        var folder = album.ArchiveFolder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = album.Title;
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var resolved = Path.IsPathRooted(folder)
            ? folder
            : Path.Combine(_settings.ArchiveRootPath ?? string.Empty, folder);
        return Directory.Exists(resolved) ? resolved : null;
    }

    /// <summary>
    /// Returns the absolute path of the disc folder under <paramref name="albumDir"/>.
    /// Order: explicit <see cref="AlbumDisc.FolderName"/> → "Disc {n}" if multi-disc
    /// → the album directory itself (single-disc albums put FLAC/MP3 directly
    /// under the album folder).
    /// </summary>
    private static string? ResolveDiscDirectory(string albumDir, CanonAlbum album, AlbumDisc disc)
    {
        if (!string.IsNullOrWhiteSpace(disc.FolderName))
        {
            var explicitDir = Path.Combine(albumDir, disc.FolderName);
            return Directory.Exists(explicitDir) ? explicitDir : null;
        }

        if (album.Discs.Count > 1)
        {
            var defaultDir = Path.Combine(albumDir, $"Disc {disc.DiscNumber}");
            return Directory.Exists(defaultDir) ? defaultDir : null;
        }

        return albumDir;
    }

    private static bool TryConvention(
        string discDir, string formatFolder, string extension, int trackNumber,
        AudioFormat format, out AudioFileLocation hit)
    {
        hit = default;
        var formatDir = Path.Combine(discDir, formatFolder);
        if (!Directory.Exists(formatDir)) return false;

        var prefix = trackNumber.ToString("D2");
        // Match "NN ...", "NN-..." etc. — anything starting with the zero-padded
        // track number. Take the lexicographically first match for determinism.
        var match = Directory
            .EnumerateFiles(formatDir, $"{prefix}*.{extension}", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (match is null) return false;
        hit = new AudioFileLocation(match, format);
        return true;
    }
}
