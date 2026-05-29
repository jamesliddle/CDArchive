using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// User-reported regression: editing a composer's name made all of the
/// composer's pieces disappear from the Canon tree until the next app
/// restart. The DB is correct (pieces FK to the composer row); the
/// failure is purely in-memory — every <c>CanonPiece.Composer</c> and
/// <c>TrackPieceRef.Composer</c> still holds the OLD name string, so the
/// UI's name-based grouping finds zero pieces under the renamed composer.
/// <see cref="ComposerRenamePropagator"/> walks every in-memory consumer
/// of the composer-name string and updates it.
/// </summary>
public class ComposerRenamePropagatorTests
{
    [Fact]
    public void Propagate_NoChange_WhenNamesEqual_ReturnsZero()
    {
        var pieces = new List<CanonPiece> { new() { Composer = "X", Title = "T" } };
        Assert.Equal(0, ComposerRenamePropagator.Propagate("X", "X", pieces));
        Assert.Equal("X", pieces[0].Composer);
    }

    [Fact]
    public void Propagate_NoChange_WhenEitherNameIsBlank_ReturnsZero()
    {
        var pieces = new List<CanonPiece> { new() { Composer = "X" } };
        Assert.Equal(0, ComposerRenamePropagator.Propagate("",  "X", pieces));
        Assert.Equal(0, ComposerRenamePropagator.Propagate("X", "",  pieces));
        Assert.Equal(0, ComposerRenamePropagator.Propagate(null, "X", pieces));
    }

    // ── Pieces ────────────────────────────────────────────────────────────

    [Fact]
    public void Propagate_TopLevelPieceComposer_GetsRenamed()
    {
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Symphony 9" },
            new() { Composer = "Mozart, Wolfgang Amadeus", Title = "Concerto" },
        };

        var updated = ComposerRenamePropagator.Propagate(
            "Beethoven, Ludwig van", "Beethoven, Ludwig", pieces);

        Assert.Equal(1, updated);
        Assert.Equal("Beethoven, Ludwig", pieces[0].Composer);
        Assert.Equal("Mozart, Wolfgang Amadeus", pieces[1].Composer);
    }

    [Fact]
    public void Propagate_CaseInsensitiveMatch()
    {
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "beethoven, ludwig van" },
        };

        ComposerRenamePropagator.Propagate(
            "Beethoven, Ludwig van", "Beethoven, Ludwig", pieces);

        Assert.Equal("Beethoven, Ludwig", pieces[0].Composer);
    }

    [Fact]
    public void Propagate_Subpieces_AreRenamedRecursively()
    {
        var pieces = new List<CanonPiece>
        {
            new()
            {
                Composer = "X",
                Title = "Sonata",
                Subpieces = new List<CanonPiece>
                {
                    new()
                    {
                        Composer = "X",
                        Title = "I. Allegro",
                        Subpieces = new List<CanonPiece>
                        {
                            new() { Composer = "X", Title = "(grandchild)" },
                        },
                    },
                    new() { Composer = "X", Title = "II. Adagio" },
                },
            },
        };

        var updated = ComposerRenamePropagator.Propagate("X", "Y", pieces);

        Assert.Equal(4, updated);
        Assert.Equal("Y", pieces[0].Composer);
        Assert.Equal("Y", pieces[0].Subpieces![0].Composer);
        Assert.Equal("Y", pieces[0].Subpieces[0].Subpieces![0].Composer);
        Assert.Equal("Y", pieces[0].Subpieces[1].Composer);
    }

    [Fact]
    public void Propagate_ContributorCredits_OnPieceAndVersion()
    {
        var pieces = new List<CanonPiece>
        {
            new()
            {
                Composer = "Bach, Johann Sebastian",
                Title = "Chaconne",
                Composers = new List<ComposerCredit>
                {
                    new() { Name = "Busoni, Ferruccio", Role = "arr." },
                },
                Versions = new List<CanonPieceVersion>
                {
                    new()
                    {
                        Description = "v2",
                        Composers = new List<ComposerCredit>
                        {
                            new() { Name = "Busoni, Ferruccio", Role = "rev." },
                        },
                    },
                },
            },
        };

        var updated = ComposerRenamePropagator.Propagate(
            "Busoni, Ferruccio", "Busoni, F.", pieces);

        Assert.Equal(2, updated);
        Assert.Equal("Busoni, F.", pieces[0].Composers![0].Name);
        Assert.Equal("Busoni, F.", pieces[0].Versions![0].Composers![0].Name);
    }

    // ── Album tracks ──────────────────────────────────────────────────────

    [Fact]
    public void Propagate_AlbumTrackPieceRefs_AreRenamed()
    {
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "An album",
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack>
                        {
                            new()
                            {
                                TrackNumber = 1,
                                PieceRefs = new List<TrackPieceRef>
                                {
                                    new() { Composer = "X", PieceTitle = "T1" },
                                    new() { Composer = "Other", PieceTitle = "T2" },
                                },
                            },
                            new()
                            {
                                TrackNumber = 2,
                                PieceRefs = new List<TrackPieceRef>
                                {
                                    new() { Composer = "X", PieceTitle = "T3" },
                                },
                            },
                        },
                    },
                },
            },
        };

        var updated = ComposerRenamePropagator.Propagate(
            "X", "Y", pieces: null, albums: albums);

        Assert.Equal(2, updated);
        Assert.Equal("Y", albums[0].Discs[0].Tracks[0].PieceRefs![0].Composer);
        Assert.Equal("Other", albums[0].Discs[0].Tracks[0].PieceRefs[1].Composer);
        Assert.Equal("Y", albums[0].Discs[0].Tracks[1].PieceRefs![0].Composer);
    }

    [Fact]
    public void Propagate_LooseTracks_AreRenamed()
    {
        var looseTracks = new List<AlbumTrack>
        {
            new()
            {
                TrackNumber = 0,
                PieceRefs = new List<TrackPieceRef>
                {
                    new() { Composer = "X", PieceTitle = "T" },
                },
            },
        };

        var updated = ComposerRenamePropagator.Propagate(
            "X", "Y", pieces: null, albums: null, looseTracks: looseTracks);

        Assert.Equal(1, updated);
        Assert.Equal("Y", looseTracks[0].PieceRefs![0].Composer);
    }

    [Fact]
    public void Propagate_HeadlineScenario_AllConsumersUpdated()
    {
        // User-reported flow: composer with two pieces + an album track ref.
        // After rename, the in-memory tree should still group correctly.
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Old", Title = "Sonata 1" },
            new() { Composer = "Old", Title = "Sonata 2" },
            new() { Composer = "Other", Title = "Distraction" },
        };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Album",
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack>
                        {
                            new()
                            {
                                TrackNumber = 1,
                                PieceRefs = new List<TrackPieceRef>
                                {
                                    new() { Composer = "Old", PieceTitle = "Sonata 1" },
                                },
                            },
                        },
                    },
                },
            },
        };

        var updated = ComposerRenamePropagator.Propagate("Old", "New", pieces, albums);

        Assert.Equal(3, updated);
        Assert.Equal("New",   pieces[0].Composer);
        Assert.Equal("New",   pieces[1].Composer);
        Assert.Equal("Other", pieces[2].Composer);
        Assert.Equal("New",   albums[0].Discs[0].Tracks[0].PieceRefs![0].Composer);
    }
}
