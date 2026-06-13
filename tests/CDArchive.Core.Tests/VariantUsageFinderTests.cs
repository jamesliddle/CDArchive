using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers <see cref="VariantUsageFinder.Find"/> — the scan that backs the
/// piece editor's "Recordings using this variant…" view.
/// </summary>
public class VariantUsageFinderTests
{
    private static AlbumTrack TrackWithVariant(int trackNo, params long[] variantIds) => new()
    {
        TrackNumber = trackNo,
        PieceRefs = new List<TrackPieceRef>
        {
            new()
            {
                Composer   = "Beethoven, Ludwig van",
                PieceTitle = "Violin Concerto",
                Variants   = variantIds.Select(id => new VariantReference { Id = id }).ToList(),
            },
        },
    };

    private static CanonAlbum AlbumWith(string title, params AlbumTrack[] tracks) => new()
    {
        Title = title,
        Discs = new List<AlbumDisc> { new() { DiscNumber = 1, Tracks = tracks.ToList() } },
    };

    [Fact]
    public void Find_ReturnsHitsForAlbumAndLooseTracks_MatchingTheVariant()
    {
        var album = AlbumWith("VC album",
            TrackWithVariant(1, 7),        // uses variant 7
            TrackWithVariant(2, 9));       // uses variant 9 only
        var loose = TrackWithVariant(0, 7); // loose track also uses variant 7

        var hits = VariantUsageFinder.Find(new[] { album }, new[] { loose }, variantId: 7);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Album == album && h.Track.TrackNumber == 1);
        Assert.Contains(hits, h => h.IsLooseTrack && h.Track.TrackNumber == 0);
        Assert.DoesNotContain(hits, h => h.Track.TrackNumber == 2);
    }

    [Fact]
    public void Find_MultipleVariantsOnOneRef_MatchesWhenAnyEquals()
    {
        var album = AlbumWith("VC", TrackWithVariant(1, 3, 7, 11));

        Assert.Single(VariantUsageFinder.Find(new[] { album }, [], 7));
        Assert.Single(VariantUsageFinder.Find(new[] { album }, [], 11));
        Assert.Empty(VariantUsageFinder.Find(new[] { album }, [], 99));
    }

    [Fact]
    public void Find_ZeroId_ReturnsEmpty()
    {
        var album = AlbumWith("VC", TrackWithVariant(1, 7));
        Assert.Empty(VariantUsageFinder.Find(new[] { album }, [], variantId: 0));
    }

    [Fact]
    public void Find_TracksWithoutVariants_AreIgnored()
    {
        var album = AlbumWith("VC", new AlbumTrack
        {
            TrackNumber = 1,
            PieceRefs = new List<TrackPieceRef>
            {
                new() { Composer = "X", PieceTitle = "Y" },   // no variants
            },
        });
        Assert.Empty(VariantUsageFinder.Find(new[] { album }, [], 7));
    }
}
