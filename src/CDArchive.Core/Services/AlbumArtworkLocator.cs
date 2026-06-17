using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Default <see cref="IAlbumArtworkLocator"/>. Resolution order:
///   1. Disc-specific folder (multi-disc albums only) — so per-disc artwork
///      wins when present.
///   2. The album's archive folder root.
/// Within each folder the first existing file matching a conventional cover
/// name (see <see cref="CoverBaseNames"/>) × a supported extension (see
/// <see cref="ImageExtensions"/>) is returned, in that preference order.
///
/// <para>
/// Album-folder resolution mirrors <see cref="ArchiveAudioLocator"/>:
/// <see cref="CanonAlbum.ArchiveFolder"/> (or the album <see cref="CanonAlbum.Title"/>
/// as the default folder name), absolute-as-is or combined under
/// <see cref="IArchiveSettings.ArchiveRootPath"/>.
/// </para>
///
/// <para>
/// Caching (mirrors Rework H8): the per-folder file listing is cached so a
/// repeated scroll of the Albums list (or auto-advance hitting the same album)
/// doesn't re-enumerate the disk. Misses are cached too. The cache
/// auto-invalidates when <see cref="IArchiveSettings.ArchiveRootPath"/> drifts,
/// and can be cleared explicitly via <see cref="Invalidate"/>.
/// </para>
///
/// <para>
/// Exposes a static <see cref="Current"/> accessor (set by the constructor)
/// because WPF value converters are built from XAML and can't be DI-injected —
/// the same pattern <see cref="PieceReferenceIndex.Current"/> uses.
/// </para>
/// </summary>
public sealed class AlbumArtworkLocator : IAlbumArtworkLocator
{
    /// <summary>Conventional cover-image base names, in preference order.</summary>
    private static readonly string[] CoverBaseNames =
        { "cover", "folder", "front", "album", "albumart" };

    /// <summary>Supported image extensions (WPF-decodable), in preference order.</summary>
    private static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp" };

    private readonly IArchiveSettings _settings;
    private readonly IArchiveAudioLocator _audioLocator;
    // Reads the embedded front-cover picture bytes from an audio file (null when
    // none / unreadable). Injectable so the priority logic is unit-testable
    // without shipping a binary audio asset; defaults to the TagLib reader.
    private readonly Func<string, byte[]?> _embeddedReader;

    private readonly object _lock = new();
    // Folder path -> (lowercased filename -> full path) for every file in it.
    // A null value records a folder that doesn't exist (so the miss is cached).
    private readonly Dictionary<string, Dictionary<string, string>?> _folderFiles =
        new(StringComparer.OrdinalIgnoreCase);
    // Audio file path -> embedded picture bytes (or null). Caches the TagLib
    // read so re-resolving (e.g. a list re-render) doesn't re-open the file.
    private readonly Dictionary<string, byte[]?> _embeddedCache =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _cachedArchiveRoot;

    /// <summary>
    /// The most recently constructed locator. Set by the constructor so WPF
    /// converters (which can't be injected) can reach it. In production there's
    /// a single DI singleton, so this is that instance.
    /// </summary>
    public static IAlbumArtworkLocator? Current { get; private set; }

    public AlbumArtworkLocator(
        IArchiveSettings settings,
        IArchiveAudioLocator audioLocator,
        Func<string, byte[]?>? embeddedReader = null)
    {
        _settings = settings;
        _audioLocator = audioLocator;
        _embeddedReader = embeddedReader ?? ReadEmbeddedPicture;
        _cachedArchiveRoot = settings.ArchiveRootPath;
        Current = this;
    }

    public void Invalidate()
    {
        lock (_lock)
        {
            _folderFiles.Clear();
            _embeddedCache.Clear();
            _cachedArchiveRoot = _settings.ArchiveRootPath;
        }
    }

    public AlbumArtwork? Resolve(CanonAlbum album, AlbumDisc? disc = null, AlbumTrack? track = null)
    {
        if (album is null) return null;

        // Cheap auto-invalidation — cached paths are rooted in ArchiveRootPath.
        var currentRoot = _settings.ArchiveRootPath;
        if (!string.Equals(currentRoot, _cachedArchiveRoot, StringComparison.OrdinalIgnoreCase))
            Invalidate();

        // 1. Embedded picture in the (given or first) track's audio file.
        var audioPath = ResolveAudioFile(album, disc, track);
        if (audioPath is not null)
        {
            var bytes = EmbeddedCached(audioPath);
            if (bytes is { Length: > 0 })
                return AlbumArtwork.FromEmbedded(audioPath, bytes);
        }

        // 2/3. Image file in the album folder (disc folder preferred when given).
        var albumDir = ResolveAlbumDirectory(album);
        if (albumDir is null) return null;

        if (disc is not null && album.Discs.Count > 1)
        {
            var discDir = ResolveDiscDirectory(albumDir, disc);
            if (discDir is not null)
            {
                var discHit = FindCover(discDir);
                if (discHit is not null) return AlbumArtwork.FromFile(discHit);
            }
        }

        var rootHit = FindCover(albumDir);
        return rootHit is null ? null : AlbumArtwork.FromFile(rootHit);
    }

    /// <summary>
    /// Resolves the audio file to read embedded art from: the given
    /// disc+track when supplied (the player's current track), else the album's
    /// first track in document order. Returns null when nothing resolves.
    /// </summary>
    private string? ResolveAudioFile(CanonAlbum album, AlbumDisc? disc, AlbumTrack? track)
    {
        if (disc is not null && track is not null)
            return _audioLocator.Resolve(album, disc, track)?.Path;

        foreach (var d in album.Discs)
            foreach (var t in d.Tracks)
            {
                var hit = _audioLocator.Resolve(album, d, t);
                if (hit is not null) return hit.Value.Path;
            }
        return null;
    }

    private byte[]? EmbeddedCached(string audioPath)
    {
        lock (_lock)
        {
            if (_embeddedCache.TryGetValue(audioPath, out var cached)) return cached;
        }

        var data = _embeddedReader(audioPath);
        lock (_lock) _embeddedCache[audioPath] = data;
        return data;
    }

    /// <summary>
    /// Default embedded-picture reader: the front cover (or first) picture from
    /// the file's tags via TagLib. Returns null on any failure (corrupt file,
    /// locked, unsupported, no picture).
    /// </summary>
    private static byte[]? ReadEmbeddedPicture(string audioPath)
    {
        try
        {
            using var file = TagLib.File.Create(audioPath);
            var pics = file.Tag.Pictures;
            if (pics is null || pics.Length == 0) return null;

            var pic = Array.Find(pics, p => p.Type == TagLib.PictureType.FrontCover) ?? pics[0];
            var data = pic.Data?.Data;
            return data is { Length: > 0 } ? data : null;
        }
        catch
        {
            return null;
        }
    }

    public AlbumArtwork? ResolveFromTrack(AlbumTrack track)
    {
        if (track is null) return null;

        // Loose tracks have no album folder/convention — only the per-track
        // override paths. Honour the preferred-format order (matches the audio
        // locator + PlayerViewModel.PlayLooseTrack).
        var prefer = _settings.PreferredAudioFormat;
        var first  = prefer == PreferredAudioFormat.Flac ? track.FlacPath : track.Mp3Path;
        var second = prefer == PreferredAudioFormat.Flac ? track.Mp3Path  : track.FlacPath;

        var path = PickExisting(first) ?? PickExisting(second);
        if (path is null) return null;

        var bytes = EmbeddedCached(path);
        return bytes is { Length: > 0 } ? AlbumArtwork.FromEmbedded(path, bytes) : null;
    }

    private static string? PickExisting(string? p) =>
        !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? p : null;

    /// <summary>Mirrors <see cref="ArchiveAudioLocator.ResolveAlbumDirectory"/>.</summary>
    private string? ResolveAlbumDirectory(CanonAlbum album)
    {
        var folder = album.ArchiveFolder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = album.Title;
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var resolved = Path.IsPathRooted(folder)
            ? folder
            : Path.Combine(_settings.ArchiveRootPath ?? string.Empty, folder);
        return resolved;
    }

    private string? ResolveDiscDirectory(string albumDir, AlbumDisc disc)
    {
        if (!string.IsNullOrWhiteSpace(disc.FolderName))
            return Path.Combine(albumDir, disc.FolderName);

        foreach (var candidate in DiscFolderConventions.CandidateNames(disc.DiscNumber))
        {
            var dir = Path.Combine(albumDir, candidate);
            if (LoadFolderCached(dir) is not null)
                return dir;
        }
        return null;
    }

    private string? FindCover(string dir)
    {
        var files = LoadFolderCached(dir);
        if (files is null) return null;

        // 1. Conventionally-named cover (cover.jpg / folder.jpg / …) wins.
        foreach (var baseName in CoverBaseNames)
        foreach (var ext in ImageExtensions)
        {
            if (files.TryGetValue(baseName + ext, out var path))
                return path;
        }

        // 2. Fallback: real archives often store a single cover scan named with
        //    an arbitrary download id (e.g. "71KMUqGkSwL.jpg"), not "cover.jpg".
        //    Use the only image in the folder; if there are several, the largest
        //    (cover scans are typically bigger than booklet thumbnails).
        var images = files.Values
            .Where(p => ImageExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (images.Count == 1) return images[0];
        if (images.Count > 1)
        {
            return images
                .Select(p => (Path: p, Size: SafeLength(p)))
                .OrderByDescending(x => x.Size)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .First().Path;
        }
        return null;
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// Cached listing of a folder's files keyed by lowercased filename. Returns
    /// null (and caches it) when the folder doesn't exist.
    /// </summary>
    private Dictionary<string, string>? LoadFolderCached(string dir)
    {
        lock (_lock)
        {
            if (_folderFiles.TryGetValue(dir, out var cached)) return cached;
        }

        Dictionary<string, string>? map = null;
        if (Directory.Exists(dir))
        {
            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
                map[Path.GetFileName(f)] = f;
        }

        lock (_lock) _folderFiles[dir] = map;
        return map;
    }
}
