using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CDArchive.App.Helpers;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Converters;

/// <summary>
/// Resolves a <see cref="CanonAlbum"/> to its cover-art <see cref="ImageSource"/>
/// for thumbnail display in album lists. Returns null when the album has no
/// artwork on disk, so the host control can show a placeholder.
///
/// <para>
/// Reads <see cref="AlbumArtworkLocator.Current"/> (a static accessor) because
/// WPF value converters are constructed from XAML and can't be DI-injected —
/// the same pattern as <see cref="HitCountBadgeConverter"/>'s use of
/// <see cref="PieceReferenceIndex.Current"/>.
/// </para>
///
/// <para>
/// <c>ConverterParameter</c> optionally sets the decode width in pixels
/// (default 80 — a 40px slot at up to 2× DPI).
/// </para>
/// </summary>
public sealed class AlbumArtworkConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Inputs: a CanonAlbum (album lists), an already-resolved AlbumArtwork
        // (the player), or an AlbumTrackRow (tracks list — album art for an
        // album-bound row, or embedded art from the loose track's own file).
        var locator = AlbumArtworkLocator.Current;
        var art = value switch
        {
            AlbumArtwork resolved => resolved,
            CanonAlbum album      => locator?.Resolve(album),
            AlbumTrackRow row     => row.Album is not null
                                        ? locator?.Resolve(row.Album)
                                        : locator?.ResolveFromTrack(row.Track),
            _                     => null
        };
        return ArtworkImageCache.Load(art, ParseWidth(parameter));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static int ParseWidth(object? parameter)
    {
        if (parameter is int i) return i;
        if (parameter is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
            return w;
        return 80;
    }
}
