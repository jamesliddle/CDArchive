using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="ItunesImporter"/>. Focused on the edge cases that
/// produced data-loss bugs in the original importer: missing iTunes Album
/// field, missing iTunes TrackNumber, duplicate (disc, track#) tuples.
/// </summary>
public class ItunesImporterTests
{
    private static ItunesTrack Track(
        int trackId,
        string name,
        string? album = null,
        int? trackNumber = null,
        int? discNumber = null,
        string? composer = null)
        => new(
            TrackId:      trackId,
            PersistentId: null,
            DiscNumber:   discNumber,
            TrackNumber:  trackNumber,
            Name:         name,
            DurationMs:   180_000,
            Genre:        null,
            Composer:     composer,
            Album:        album,
            AlbumArtist:  null,
            Artist:       null,
            DateAdded:    DateTime.UtcNow,
            Location:     null);

    /// <summary>
    /// Tracks with no Album field each become their own one-track album titled
    /// after the track Name — not a single synthetic "(Unknown album)".
    /// </summary>
    [Fact]
    public void StandaloneTracks_BecomePerTrackAlbums()
    {
        var tracks = new[]
        {
            Track(1, "Souvenir d'une nuit d'été à Madrid", composer: "Glinka, Mikhail (1804-1857)", trackNumber: 1),
            Track(2, "An Outdoor Overture",                composer: "Copland, Aaron (1900-1990)",  trackNumber: 1),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var result = ItunesImporter.Import(tracks, composers, pieces);

        Assert.Equal(2, result.NewAlbums.Count);
        var titles = result.NewAlbums.Select(a => a.Title).OrderBy(s => s).ToList();
        Assert.Equal("An Outdoor Overture",                          titles[0]);
        Assert.Equal("Souvenir d'une nuit d'été à Madrid",            titles[1]);
        Assert.All(result.NewAlbums, a => Assert.Single(a.Discs[0].Tracks));
    }

    /// <summary>
    /// A single standalone track with NO iTunes TrackNumber must still save
    /// with a positive track number. The track editor validation rejects 0
    /// with "Track number must be a positive integer", so an albumless MP3
    /// without a track number would otherwise be locked out of further edits.
    /// </summary>
    [Fact]
    public void StandaloneTrack_WithNullTrackNumber_GetsPositiveNumber()
    {
        var tracks = new[]
        {
            Track(1, "Standalone Track", composer: "Glinka, Mikhail (1804-1857)", trackNumber: null),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var only = Assert.Single(result.NewAlbums);
        var disc = Assert.Single(only.Discs);
        var trk  = Assert.Single(disc.Tracks);
        Assert.True(trk.TrackNumber >= 1, $"TrackNumber was {trk.TrackNumber}, expected >= 1");
    }

    /// <summary>
    /// A bona-fide album with duplicate iTunes track numbers gets renumbered
    /// sequentially — UNIQUE(disc_id, track_number) on save would otherwise fail.
    /// </summary>
    [Fact]
    public void Album_WithDuplicateTrackNumbers_RenumbersSequentially()
    {
        var tracks = new[]
        {
            Track(1, "First",  album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
            Track(2, "Second", album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
            Track(3, "Third",  album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var only = Assert.Single(result.NewAlbums);
        var disc = Assert.Single(only.Discs);
        Assert.Equal(3, disc.Tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, disc.Tracks.Select(t => t.TrackNumber));
    }

    /// <summary>
    /// Mixed scenario: a real album where some tracks have track numbers and
    /// some don't. Renumber-on-any-non-positive kicks the whole disc into
    /// sequential numbering so no track lands at 0.
    /// </summary>
    [Fact]
    public void Album_WithSomeMissingTrackNumbers_RenumbersWholeDisc()
    {
        var tracks = new[]
        {
            Track(1, "A", album: "Mix", trackNumber: 1,    composer: "X, Y (1900-2000)"),
            Track(2, "B", album: "Mix", trackNumber: null, composer: "X, Y (1900-2000)"),
            Track(3, "C", album: "Mix", trackNumber: 3,    composer: "X, Y (1900-2000)"),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var only = Assert.Single(result.NewAlbums);
        var disc = Assert.Single(only.Discs);
        Assert.All(disc.Tracks, t => Assert.True(t.TrackNumber >= 1));
        Assert.Equal(disc.Tracks.Count, disc.Tracks.Select(t => t.TrackNumber).Distinct().Count());
    }
}
