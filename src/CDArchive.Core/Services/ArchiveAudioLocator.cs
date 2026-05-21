using CDArchive.Core.Helpers;
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
///
/// <para>
/// Caching (Rework H8): pre-fix the locator issued ~6 filesystem syscalls
/// per <see cref="Resolve"/> call (3× <see cref="Directory.Exists"/> +
/// 2× <see cref="Directory.EnumerateFiles"/> with a glob). For a single
/// album auto-advance through 20 tracks that's ~120 syscalls; on a network
/// drive or at the 3,000-CD scale it becomes a real cost. The locator now
/// caches the two probe shapes:
/// <list type="bullet">
///   <item><c>Directory.Exists</c> per resolved path — for the album
///     directory, disc directory, and format subdirectory.</item>
///   <item>The full <c>.flac</c> / <c>.mp3</c> file list per format
///     directory — the per-track <c>"NN*"</c> match is then an in-memory
///     prefix scan over the cached list.</item>
/// </list>
/// Cache lives for the lifetime of the singleton. It auto-invalidates when
/// <see cref="IArchiveSettings.ArchiveRootPath"/> changes between calls
/// (the paths it caches are constructed from that root). Callers that
/// touch the underlying filesystem outside the app (album-folder rename,
/// archive rescan) can call <see cref="Invalidate"/> explicitly.
/// </para>
/// </summary>
public sealed class ArchiveAudioLocator : IArchiveAudioLocator
{
    private readonly IArchiveSettings _settings;
    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _dirExists =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FormatDirEntry> _formatDirs =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _cachedArchiveRoot;

    /// <summary>
    /// Test-visible counter: incremented for each real filesystem syscall
    /// (<see cref="Directory.Exists"/> or <see cref="Directory.EnumerateFiles"/>)
    /// the locator issues. Cache hits don't increment it. Used by the
    /// cache-behaviour regression tests to assert the cache is actually
    /// short-circuiting; internal so production callers can't accidentally
    /// depend on it.
    /// </summary>
    internal int FilesystemProbeCount { get; private set; }

    public ArchiveAudioLocator(IArchiveSettings settings)
    {
        _settings = settings;
        _cachedArchiveRoot = settings.ArchiveRootPath;
    }

    /// <summary>
    /// Clears the per-album/disc filesystem cache. Subsequent
    /// <see cref="Resolve"/> calls re-probe the disk. Call this when
    /// something the locator can't observe has changed: archive folder
    /// renamed in the album editor, files added/removed from the archive
    /// outside the app, a full rescan completing, etc.
    /// </summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _dirExists.Clear();
            _formatDirs.Clear();
            FilesystemProbeCount = 0;
            _cachedArchiveRoot = _settings.ArchiveRootPath;
        }
    }

    public AudioFileLocation? Resolve(CanonAlbum album, AlbumDisc disc, AlbumTrack track)
    {
        // Cheap auto-invalidation: every cached path is rooted in
        // ArchiveRootPath, so a settings drift since the last build means
        // every entry is potentially stale.
        var currentRoot = _settings.ArchiveRootPath;
        if (!string.Equals(currentRoot, _cachedArchiveRoot, StringComparison.OrdinalIgnoreCase))
            Invalidate();

        var prefer = _settings.PreferredAudioFormat;

        // 1. Per-track overrides — preferred first, then the other. Not
        // cached (File.Exists on a specific path is already a single
        // syscall; the locator-cache shape isn't a fit here).
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
        return DirExistsCached(resolved) ? resolved : null;
    }

    /// <summary>
    /// Returns the absolute path of the disc folder under <paramref name="albumDir"/>.
    /// Order: explicit <see cref="AlbumDisc.FolderName"/> → unpadded
    /// <c>"Disc N"</c> → padded <c>"Disc NN"</c> → the album directory
    /// itself (single-disc albums put FLAC/MP3 directly under the album
    /// folder).
    ///
    /// <para>
    /// Rework H46: pre-fix this only tried <c>$"Disc {disc.DiscNumber}"</c>
    /// (unpadded), so a 10+ disc box set scaffolded with padded names
    /// (<c>Disc 01</c>) silently failed playback on discs 1-9 — the
    /// locator's convention path didn't match the on-disk folder. Now we
    /// walk <see cref="DiscFolderConventions.CandidateNames"/>, which
    /// yields both forms; whichever exists wins.
    /// </para>
    /// </summary>
    private string? ResolveDiscDirectory(string albumDir, CanonAlbum album, AlbumDisc disc)
    {
        if (!string.IsNullOrWhiteSpace(disc.FolderName))
        {
            var explicitDir = Path.Combine(albumDir, disc.FolderName);
            return DirExistsCached(explicitDir) ? explicitDir : null;
        }

        if (album.Discs.Count > 1)
        {
            foreach (var candidate in DiscFolderConventions.CandidateNames(disc.DiscNumber))
            {
                var dir = Path.Combine(albumDir, candidate);
                if (DirExistsCached(dir))
                    return dir;
            }
            return null;
        }

        return albumDir;
    }

    private bool TryConvention(
        string discDir, string formatFolder, string extension, int trackNumber,
        AudioFormat format, out AudioFileLocation hit)
    {
        hit = default;
        var formatDir = Path.Combine(discDir, formatFolder);

        var entry = LoadFormatDirCached(formatDir, extension);
        if (!entry.Exists) return false;

        var prefix = trackNumber.ToString("D2");
        // The cached file list is sorted ordinal-case-insensitive; the first
        // entry starting with the zero-padded track number wins, matching
        // the deterministic behaviour of the original glob+OrderBy form.
        string? match = null;
        foreach (var path in entry.SortedFiles)
        {
            if (Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                match = path;
                break;
            }
        }

        if (match is null) return false;
        hit = new AudioFileLocation(match, format);
        return true;
    }

    /// <summary>
    /// Cached <see cref="Directory.Exists"/>. Production callers will see
    /// each unique directory probed at most once between
    /// <see cref="Invalidate"/> calls (or settings drift).
    /// </summary>
    private bool DirExistsCached(string path)
    {
        lock (_lock)
        {
            if (_dirExists.TryGetValue(path, out var cached)) return cached;
        }
        FilesystemProbeCount++;
        var exists = Directory.Exists(path);
        lock (_lock) _dirExists[path] = exists;
        return exists;
    }

    /// <summary>
    /// Cached enumeration of a single format directory's audio files (the
    /// whole list, not a per-prefix glob — the prefix match is done in
    /// memory after the cache hit). When the directory doesn't exist the
    /// entry records that so future probes also short-circuit.
    /// </summary>
    private FormatDirEntry LoadFormatDirCached(string formatDir, string extension)
    {
        lock (_lock)
        {
            if (_formatDirs.TryGetValue(formatDir, out var cached)) return cached;
        }

        FilesystemProbeCount++;
        if (!Directory.Exists(formatDir))
        {
            var miss = new FormatDirEntry(false, Array.Empty<string>());
            lock (_lock) _formatDirs[formatDir] = miss;
            return miss;
        }

        FilesystemProbeCount++;
        // Pull every file with the target extension once. Sorted
        // case-insensitive so the per-track prefix scan is deterministic
        // — matches the OrderBy(StringComparer.OrdinalIgnoreCase) of the
        // pre-cache version.
        var files = Directory.EnumerateFiles(formatDir, $"*.{extension}", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entry = new FormatDirEntry(true, files);
        lock (_lock) _formatDirs[formatDir] = entry;
        return entry;
    }

    private sealed record FormatDirEntry(bool Exists, IReadOnlyList<string> SortedFiles);
}
