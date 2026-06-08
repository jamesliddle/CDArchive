using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Unit tests for <see cref="PieceReferenceIndex"/>'s handling of the marker /
/// range ref fields. Builds small in-memory canons + albums (no SQLite
/// involvement) so the resolver can be exercised without I/O.
/// </summary>
public class MarkerResolverTests
{
    /// <summary>
    /// La bohème Act III: the Toscanini-style range "3j → 3l" should credit
    /// movements 3j, 3k, AND 3l once each — siblings between (and including)
    /// the start and end leaves.
    /// </summary>
    [Fact]
    public void Range_AcrossSiblings_CreditsEveryLeafInTheRange()
    {
        // Three sibling subpieces under Act III.
        var sub3j = MakeSubpiece("3j. Addio, senza rancor!");
        var sub3k = MakeSubpiece("3k. Dunque è proprio finita!");
        var sub3l = MakeSubpiece("3l. Addio, dolce svegliare alla mattina");
        var act3 = new CanonPiece { Title = "Act III", Subpieces = [sub3j, sub3k, sub3l] };
        var opera = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [act3],
        };

        var album = new CanonAlbum
        {
            Title = "La bohème (Toscanini)",
            Discs = [new AlbumDisc
            {
                DiscNumber = 1,
                Tracks = [new AlbumTrack
                {
                    TrackNumber = 12,
                    PieceRefs   = [new TrackPieceRef
                    {
                        Composer        = "Puccini, Giacomo",
                        PieceTitle      = "La bohème",
                        SubpiecePath    = ["Act III", sub3j.Title!],
                        EndSubpiecePath = ["Act III", sub3l.Title!],
                    }],
                }],
            }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { opera }, new[] { album });

        // Each of the three sibling subpieces should show one album hit.
        Assert.Equal(1, index.CountForPiece(sub3j));
        Assert.Equal(1, index.CountForPiece(sub3k));
        Assert.Equal(1, index.CountForPiece(sub3l));
        // The wrapping Act III also gets credited (it's an ancestor on the
        // start path) — covered by the standard ancestor-credit loop.
        Assert.Equal(1, index.CountForPiece(act3));
    }

    /// <summary>
    /// A non-range ref (no <see cref="TrackPieceRef.EndSubpiecePath"/>) credits
    /// only the start leaf. Sanity check that the new range code path doesn't
    /// regress the common single-segment case.
    /// </summary>
    [Fact]
    public void NonRangeRef_CreditsOnlyTheStartLeaf()
    {
        var sub3j = MakeSubpiece("3j. Addio, senza rancor!");
        var sub3k = MakeSubpiece("3k. Dunque è proprio finita!");
        var act3 = new CanonPiece { Title = "Act III", Subpieces = [sub3j, sub3k] };
        var opera = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [act3],
        };

        var album = new CanonAlbum
        {
            Title = "La bohème (Beecham)",
            Discs = [new AlbumDisc
            {
                DiscNumber = 1,
                Tracks = [new AlbumTrack
                {
                    TrackNumber = 14,
                    PieceRefs   = [new TrackPieceRef
                    {
                        Composer     = "Puccini, Giacomo",
                        PieceTitle   = "La bohème",
                        SubpiecePath = ["Act III", sub3j.Title!],
                    }],
                }],
            }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { opera }, new[] { album });

        Assert.Equal(1, index.CountForPiece(sub3j));
        Assert.Equal(0, index.CountForPiece(sub3k));     // no range → no spillover
    }

    /// <summary>
    /// Marker anchors travel on the resolved hit but don't change which
    /// subpieces are credited — markers describe where <em>in</em> a subpiece
    /// the recording starts, not which subpiece is referenced.
    /// </summary>
    [Fact]
    public void MarkerAnchor_DoesNotChangeAttribution_AndPersistsOnHit()
    {
        var marker = new MusicalMarker
        {
            Id    = 42,
            Kind  = MarkerKind.Section,
            Value = "Addio, senza rancor!",
        };
        var sub = MakeSubpiece("3j. Addio, senza rancor!");
        sub.Markers = [marker];

        var act = new CanonPiece { Title = "Act III", Subpieces = [sub] };
        var opera = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [act],
        };

        var pieceRef = new TrackPieceRef
        {
            Composer     = "Puccini, Giacomo",
            PieceTitle   = "La bohème",
            SubpiecePath = ["Act III", sub.Title!],
            StartMarker  = new MarkerReference { Id = 42, Kind = MarkerKind.Section },
        };
        var album = new CanonAlbum
        {
            Title = "La bohème",
            Discs = [new AlbumDisc
            {
                DiscNumber = 1,
                Tracks = [new AlbumTrack { TrackNumber = 12, PieceRefs = [pieceRef] }],
            }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { opera }, new[] { album });

        // Subpiece is credited normally — marker doesn't gate it.
        Assert.Equal(1, index.CountForPiece(sub));

        // The PieceAlbumHit retains the original ref (with its StartMarker
        // intact) so display layers can show "starts at first line ...".
        var hits = index.HitsForPiece(sub);
        Assert.Single(hits);
        Assert.NotNull(hits[0].Ref.StartMarker);
        Assert.Equal(42, hits[0].Ref.StartMarker!.Id);
    }

    /// <summary>
    /// A range whose endpoints don't share a parent (malformed) gracefully
    /// degrades — we still credit the start path and don't crash.
    /// </summary>
    [Fact]
    public void Range_WhenEndpointsDontShareParent_DegradesToStartOnly()
    {
        var aria1 = MakeSubpiece("Aria 1");
        var aria2 = MakeSubpiece("Aria 2");
        var act1  = new CanonPiece { Title = "Act I",  Subpieces = [aria1] };
        var act2  = new CanonPiece { Title = "Act II", Subpieces = [aria2] };
        var opera = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [act1, act2],
        };

        var album = new CanonAlbum
        {
            Title = "La bohème",
            Discs = [new AlbumDisc
            {
                DiscNumber = 1,
                Tracks = [new AlbumTrack
                {
                    TrackNumber = 1,
                    PieceRefs   = [new TrackPieceRef
                    {
                        Composer        = "Puccini, Giacomo",
                        PieceTitle      = "La bohème",
                        SubpiecePath    = ["Act I",  aria1.Title!],
                        EndSubpiecePath = ["Act II", aria2.Title!], // different parents
                    }],
                }],
            }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild(new[] { opera }, new[] { album });

        Assert.Equal(1, index.CountForPiece(aria1));     // start still credits
        Assert.Equal(0, index.CountForPiece(aria2));     // misaligned end ignored
    }

    /// <summary>Helper — build a subpiece whose title matches its label.</summary>
    private static CanonPiece MakeSubpiece(string title) => new() { Title = title };
}
