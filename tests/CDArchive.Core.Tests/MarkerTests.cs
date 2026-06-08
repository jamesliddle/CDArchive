using System.Text.Json;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Pure-model tests for the marker types (no SQLite dependency). Covers
/// JSON round-trip of <see cref="MusicalMarker"/>, range refs, and the
/// resolver's range-credit semantics.
/// </summary>
public class MarkerTests
{
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void MusicalMarker_RoundTripsThroughJson_PreservingAllFields()
    {
        var original = new MusicalMarker
        {
            Id          = 42,
            Kind        = MarkerKind.RehearsalMark,
            Value       = "17",
            BarNumber   = 543,
            Number      = 4,
            Description = "Tempo change at 17",
            SubMarkers  = [
                new MusicalMarker { Kind = MarkerKind.Tempo, Value = "Allegro" },
            ],
        };

        var json = JsonSerializer.Serialize(original, WriteOpts);
        var roundTripped = JsonSerializer.Deserialize<MusicalMarker>(json, ReadOpts);

        Assert.NotNull(roundTripped);
        Assert.Equal(original.Id,          roundTripped!.Id);
        Assert.Equal(original.Kind,        roundTripped.Kind);
        Assert.Equal(original.Value,       roundTripped.Value);
        Assert.Equal(original.BarNumber,   roundTripped.BarNumber);
        Assert.Equal(original.Number,      roundTripped.Number);
        Assert.Equal(original.Description, roundTripped.Description);
        Assert.NotNull(roundTripped.SubMarkers);
        Assert.Single(roundTripped.SubMarkers!);
        Assert.Equal("Allegro", roundTripped.SubMarkers![0].Value);
    }

    [Fact]
    public void MusicalMarker_OmitsDefaultId_FromJsonOutput()
    {
        // A freshly-constructed marker (Id == 0) should not write its id, so
        // human-edited JSON files don't carry a meaningless "id": 0 noise.
        var marker = new MusicalMarker { Kind = MarkerKind.Section, Value = "Wenn mein Schatz" };
        var json = JsonSerializer.Serialize(marker, WriteOpts);
        Assert.DoesNotContain("\"id\"", json);
        Assert.Contains("\"kind\":", json);
        Assert.Contains("\"value\":", json);
    }

    [Fact]
    public void MarkerKind_SerializesAsString_InJson()
    {
        var marker = new MusicalMarker { Kind = MarkerKind.RehearsalMark, Value = "A" };
        var json = JsonSerializer.Serialize(marker, WriteOpts);
        // String form lets DB Browser users read the value at a glance.
        Assert.Contains("\"kind\": \"RehearsalMark\"", json);
    }

    [Fact]
    public void TrackPieceRef_DisplaySummary_IncludesRangeAndMarkers()
    {
        var pieceRef = new TrackPieceRef
        {
            Composer        = "Puccini, Giacomo",
            PieceTitle      = "La bohème",
            SubpiecePath    = ["Act III", "3j. Addio, senza rancor!"],
            EndSubpiecePath = ["Act III", "3l. Addio, dolce svegliare alla mattina"],
            StartMarker     = new MarkerReference { Kind = MarkerKind.Section, Value = "Addio, senza rancor!" },
            EndMarker       = new MarkerReference { Kind = MarkerKind.Section, Value = "Addio, dolce svegliare alla mattina" },
        };

        var summary = pieceRef.DisplaySummary;
        Assert.Contains("La bohème",  summary);
        Assert.Contains("Act III",    summary);
        Assert.Contains("3j",         summary);
        Assert.Contains("through",    summary);
        Assert.Contains("3l",         summary);
        Assert.Contains("from",       summary);
        Assert.Contains("Addio",      summary);
    }

    [Fact]
    public void TrackPieceRef_IsRange_TrueOnlyWhenEndSubpiecePathSet()
    {
        Assert.False(new TrackPieceRef { SubpiecePath = ["Allegro"] }.IsRange);
        Assert.False(new TrackPieceRef { EndSubpiecePath = [] }.IsRange);
        Assert.True(new TrackPieceRef
        {
            SubpiecePath    = ["Act III"],
            EndSubpiecePath = ["Act IV"],
        }.IsRange);
    }

    [Fact]
    public void RangeRef_CreditsEverySiblingInRange_Inclusive()
    {
        // Build a synthetic opera-shaped piece tree:
        //   La bohème (Puccini)
        //     Act III (subpiece-of-piece)
        //       3i. … 3j. … 3k. … 3l. … 3m. (sibling subpieces under Act III)
        // Then a range ref [3j..3l] should credit 3j, 3k, 3l (but not 3i, 3m).
        var sub3i = new CanonPiece { Title = "3i. Section i" };
        var sub3j = new CanonPiece { Title = "3j. Addio, senza rancor!" };
        var sub3k = new CanonPiece { Title = "3k. Dunque è proprio finita!" };
        var sub3l = new CanonPiece { Title = "3l. Addio, dolce svegliare" };
        var sub3m = new CanonPiece { Title = "3m. Che facevi, che dicevi" };
        var actIII = new CanonPiece
        {
            Title     = "Act III",
            Subpieces = [sub3i, sub3j, sub3k, sub3l, sub3m],
        };
        var leBoheme = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [actIII],
        };

        var index = new PieceReferenceIndex();
        var album = new CanonAlbum
        {
            Title = "La bohème (Toscanini)",
            Discs =
            [
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    [
                        new AlbumTrack
                        {
                            TrackNumber = 7,
                            PieceRefs = [new TrackPieceRef
                            {
                                Composer        = "Puccini, Giacomo",
                                PieceTitle      = "La bohème",
                                SubpiecePath    = ["Act III", "3j. Addio, senza rancor!"],
                                EndSubpiecePath = ["Act III", "3l. Addio, dolce svegliare"],
                            }],
                        }
                    ],
                }
            ],
        };

        index.Rebuild([leBoheme], [album]);

        // 3j, 3k, 3l should each have one hit.
        Assert.Equal(1, index.CountForPiece(sub3j));
        Assert.Equal(1, index.CountForPiece(sub3k));
        Assert.Equal(1, index.CountForPiece(sub3l));
        // 3i and 3m should have none.
        Assert.Equal(0, index.CountForPiece(sub3i));
        Assert.Equal(0, index.CountForPiece(sub3m));
    }

    [Fact]
    public void RangeRef_OverlappingTwoTracks_BothCreditSharedSubpieces()
    {
        // Toscanini track: 3j → 3l. Beecham track: 3k → 3m.
        // Subpiece 3k and 3l appear on both → count 2 each.
        // 3j only on Toscanini → count 1. 3m only on Beecham → count 1.
        // 3i on neither → count 0.
        var sub3i = new CanonPiece { Title = "3i" };
        var sub3j = new CanonPiece { Title = "3j" };
        var sub3k = new CanonPiece { Title = "3k" };
        var sub3l = new CanonPiece { Title = "3l" };
        var sub3m = new CanonPiece { Title = "3m" };
        var actIII = new CanonPiece
        {
            Title     = "Act III",
            Subpieces = [sub3i, sub3j, sub3k, sub3l, sub3m],
        };
        var leBoheme = new CanonPiece
        {
            Composer  = "Puccini, Giacomo",
            Title     = "La bohème",
            Subpieces = [actIII],
        };

        AlbumTrack MakeTrack(string startSeg, string endSeg) => new()
        {
            TrackNumber = 1,
            PieceRefs = [new TrackPieceRef
            {
                Composer        = "Puccini, Giacomo",
                PieceTitle      = "La bohème",
                SubpiecePath    = ["Act III", startSeg],
                EndSubpiecePath = ["Act III", endSeg],
            }],
        };

        var toscanini = new CanonAlbum
        {
            Title = "Toscanini",
            Discs = [new AlbumDisc { DiscNumber = 1, Tracks = [MakeTrack("3j", "3l")] }],
        };
        var beecham = new CanonAlbum
        {
            Title = "Beecham",
            Discs = [new AlbumDisc { DiscNumber = 1, Tracks = [MakeTrack("3k", "3m")] }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild([leBoheme], [toscanini, beecham]);

        Assert.Equal(0, index.CountForPiece(sub3i));
        Assert.Equal(1, index.CountForPiece(sub3j));
        Assert.Equal(2, index.CountForPiece(sub3k));
        Assert.Equal(2, index.CountForPiece(sub3l));
        Assert.Equal(1, index.CountForPiece(sub3m));
    }

    [Fact]
    public void MarkerAnchorOnSingleSubpiece_DoesNotChangeCreditAttribution()
    {
        // A track that starts mid-subpiece (e.g. Beethoven 9 IV split at bar
        // 543) credits the same subpiece as a non-marker ref to it would.
        // The marker is descriptive only.
        var finale = new CanonPiece { Title = "IV. Presto", Number = 4 };
        var sym9 = new CanonPiece
        {
            Composer  = "Beethoven, Ludwig van",
            Title     = "Symphony #9",
            Subpieces = [finale],
        };

        var refWithoutMarker = new TrackPieceRef
        {
            Composer     = "Beethoven, Ludwig van",
            PieceTitle   = "Symphony #9",
            SubpiecePath = ["IV. Presto"],
        };
        var refWithMarker = new TrackPieceRef
        {
            Composer     = "Beethoven, Ludwig van",
            PieceTitle   = "Symphony #9",
            SubpiecePath = ["IV. Presto"],
            StartMarker  = new MarkerReference { Kind = MarkerKind.BarNumber, BarNumber = 543 },
        };

        var album = new CanonAlbum
        {
            Title = "Symphony #9 split-finale",
            Discs = [new AlbumDisc
            {
                DiscNumber = 1,
                Tracks =
                [
                    new AlbumTrack { TrackNumber = 1, PieceRefs = [refWithoutMarker] },
                    new AlbumTrack { TrackNumber = 2, PieceRefs = [refWithMarker] },
                ],
            }],
        };

        var index = new PieceReferenceIndex();
        index.Rebuild([sym9], [album]);

        // Both tracks credit the finale. CountForPiece is distinct-album
        // count (badge semantics), so it's 1 — both tracks are on the same
        // album. The raw hit list has both entries, distinguishable by their
        // markers when displayed.
        Assert.Equal(1, index.CountForPiece(finale));
        var hits = index.HitsForPiece(finale);
        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.Ref.StartMarker is null);
        Assert.Contains(hits, h => h.Ref.StartMarker?.BarNumber == 543);
    }
}
