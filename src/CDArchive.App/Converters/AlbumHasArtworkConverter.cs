using System.Globalization;
using System.Windows.Data;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.App.Converters;

/// <summary>
/// True when the album (or already-resolved <see cref="AlbumArtwork"/>) has
/// cover art. Bound to <c>ToolTipService.IsEnabled</c> so the hover-preview
/// tooltip is suppressed on rows / tracks that only show the placeholder glyph.
/// Resolution goes through the cached <see cref="AlbumArtworkLocator"/>, so this
/// adds no extra disk cost beyond what the thumbnail converter already paid.
/// </summary>
public sealed class AlbumHasArtworkConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
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
        return art is not null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
