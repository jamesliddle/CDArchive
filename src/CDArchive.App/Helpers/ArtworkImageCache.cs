using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CDArchive.Core.Services;

namespace CDArchive.App.Helpers;

/// <summary>
/// Loads cover-art image files into small, frozen <see cref="ImageSource"/>s
/// for display in the player bar and album lists, cached by (path, decode size)
/// so the same artwork isn't decoded twice while scrolling a long list.
///
/// <para>
/// Images are decoded at <c>DecodePixelWidth</c> so a 4000px scanned cover
/// doesn't sit in memory at full resolution behind a 40px thumbnail — essential
/// at the 3,000-album scale. <see cref="BitmapCacheOption.OnLoad"/> reads the
/// whole file up front and releases the handle (so the file isn't locked), and
/// <see cref="Freezable.Freeze"/> makes the result shareable across threads and
/// cheaper to render.
/// </para>
///
/// <para>
/// Only successful loads are cached. A null/missing/corrupt path returns null
/// (the caller shows a placeholder) without poisoning the cache, so artwork
/// added later is picked up once the locator re-resolves it.
/// </para>
/// </summary>
public static class ArtworkImageCache
{
    private static readonly object _lock = new();
    private static readonly Dictionary<(string Path, int Width), ImageSource> _cache = new();

    /// <summary>
    /// Returns a decoded, frozen image for the resolved <paramref name="art"/>
    /// — loaded from its file path or its embedded bytes — or null when the art
    /// is null or fails to decode. <paramref name="decodePixelWidth"/> caps the
    /// decoded width (0 = full size).
    /// </summary>
    public static ImageSource? Load(AlbumArtwork? art, int decodePixelWidth)
    {
        if (art is null) return null;

        var key = (art.CacheKey, decodePixelWidth);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            // NB: do NOT set BitmapCreateOptions.IgnoreImageCache — it targets
            // URI-keyed images and throws ArgumentNullException("key") when used
            // with a StreamSource (embedded-art bytes). We have our own cache.
            if (decodePixelWidth > 0) bmp.DecodePixelWidth = decodePixelWidth;

            if (art.EmbeddedData is { Length: > 0 } bytes)
            {
                bmp.StreamSource = new MemoryStream(bytes);
            }
            else if (!string.IsNullOrWhiteSpace(art.FilePath) && File.Exists(art.FilePath))
            {
                bmp.UriSource = new Uri(art.FilePath, UriKind.Absolute);
            }
            else
            {
                return null;
            }

            bmp.EndInit();
            bmp.Freeze(); // OnLoad has fully read the stream/file by now
            lock (_lock) _cache[key] = bmp;
            return bmp;
        }
        catch
        {
            // Corrupt image, locked file, unsupported codec — fall back to the
            // placeholder rather than crashing the list/player render.
            return null;
        }
    }

    /// <summary>Drops every cached image. Call when artwork on disk may have changed.</summary>
    public static void Clear()
    {
        lock (_lock) _cache.Clear();
    }
}
