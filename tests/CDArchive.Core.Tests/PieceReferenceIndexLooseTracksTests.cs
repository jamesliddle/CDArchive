using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// In-memory tests for <see cref="PieceReferenceIndex"/>'s loose-track support.
/// Covers:
/// <list type="bullet">
///   <item>Loose tracks contribute hits to the same dictionaries as album tracks.</item>
///   <item><see cref="PieceReferenceIndex.CountForPiece"/> dedups by container:
///     albums count once each, loose tracks count once each.</item>
///   <item><see cref="PieceAlbumHit.IsLooseTrack"/> reflects the container kind.</item>
///   <item>Set aggregation ignores loose-track hits — a loose track can't
///     satisfy "every member of a set".</item>
/// </list>
/// </summary>
public class PieceReferenceIndexLooseTracksTests
{
    private static CanonPiece Sonata(string title) => new()
    {
        Composer = "Beethoven, Ludwig van",
        Title    = title,
    };

    [Fact]
    public void LooseTrack_HitsThePieceWithNullAlbumAndDisc()
    {
        var piece  = Sonata("Für Elise");
        var pieces = new[] { piece };
        var loose  = new AlbumTrack
        {
            Description = "Loose download",
            PieceRefs   = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(pieces, Array.Empty<CanonAlbum>(), [loose]);

        var hits = index.HitsForPiece(piece);
        var hit  = Assert.Single(hits);
        Assert.Null(hit.Album);
        Assert.Null(hit.Disc);
        Assert.Same(loose, hit.Track);
        Assert.True(hit.IsLooseTrack);
    }

    [Fact]
    public void CountForPiece_TreatsAlbumsAndLooseTracksAsDistinctContainers()
    {
        var piece  = Sonata("Symphony No. 5");
        var pieces = new[] { piece };

        // Album with two tracks both pointing at the same piece — counts as 1 album.
        var album = new CanonAlbum
        {
            Title = "Test album",
            Discs =
            {
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    {
                        new AlbumTrack { TrackNumber = 1, PieceRefs = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }] },
                        new AlbumTrack { TrackNumber = 2, PieceRefs = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }] },
                    },
                },
            },
        };

        // Two loose tracks each pointing at the piece — count as 2 distinct loose containers.
        var loose1 = new AlbumTrack { Description = "Loose 1", PieceRefs = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }] };
        var loose2 = new AlbumTrack { Description = "Loose 2", PieceRefs = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }] };

        var index = new PieceReferenceIndex();
        index.Rebuild(pieces, [album], [loose1, loose2]);

        // 1 album + 2 loose tracks = 3 containers.
        Assert.Equal(3, index.CountForPiece(piece));
    }

    [Fact]
    public void Rebuild_WithoutLooseTracksArg_BehavesLikeNoLooseTracks()
    {
        // The legacy two-arg call shape stays valid and produces an index with
        // no loose-track hits. Regression guard for the optional-arg signature.
        var piece  = Sonata("Für Elise");
        var index  = new PieceReferenceIndex();
        index.Rebuild(new[] { piece }, Array.Empty<CanonAlbum>());
        Assert.Empty(index.HitsForPiece(piece));
        Assert.Equal(0, index.CountForPiece(piece));
    }

    [Fact]
    public void RebuildContainers_IncludesLooseTracksFromCachedPieceList()
    {
        // RebuildContainers reuses the piece list from the most recent Rebuild
        // and re-walks the (possibly different) albums + loose tracks. Exercises
        // the "albums saved, refresh badges" code path.
        var piece = Sonata("Für Elise");
        var loose = new AlbumTrack
        {
            Description = "Loose",
            PieceRefs   = [new TrackPieceRef { Composer = piece.Composer!, PieceTitle = piece.Title! }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { piece }, Array.Empty<CanonAlbum>());      // priming
        index.RebuildContainers(Array.Empty<CanonAlbum>(), [loose]);    // loose tracks added

        Assert.Equal(1, index.CountForPiece(piece));
        Assert.True(index.HitsForPiece(piece).Single().IsLooseTrack);
    }

    [Fact]
    public void SetAggregation_IgnoresLooseTracks()
    {
        // A set with two members (Op. 49 #1 and #2). One album carries both;
        // a loose track carries only one. The set badge should count the album
        // alone (1), not 2 (which would happen if the loose track wrongly
        // qualified despite not containing every member).
        var member1 = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Op. 49 #1" };
        var member2 = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Op. 49 #2" };
        var set = new CanonPiece
        {
            Composer  = "Beethoven, Ludwig van",
            Title     = "Two Easy Sonatas",
            Form      = "set",
            Subpieces = [member1, member2],
        };

        var albumWithBoth = new CanonAlbum
        {
            Title = "Easy Sonatas",
            Discs =
            {
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    {
                        new AlbumTrack { TrackNumber = 1, PieceRefs = [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Op. 49 #1" }] },
                        new AlbumTrack { TrackNumber = 2, PieceRefs = [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Op. 49 #2" }] },
                    },
                },
            },
        };
        var looseOnlyOneMember = new AlbumTrack
        {
            Description = "Loose Op. 49 #1",
            PieceRefs   = [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Op. 49 #1" }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { set }, [albumWithBoth], [looseOnlyOneMember]);

        // Set badge counts only the album that has every member.
        Assert.Equal(1, index.CountForPiece(set));

        // The individual members each have 1 album hit and (for #1) 1 loose track hit.
        Assert.Equal(1, index.CountForPiece(member2));
        Assert.Equal(2, index.CountForPiece(member1)); // album + loose
    }
}
