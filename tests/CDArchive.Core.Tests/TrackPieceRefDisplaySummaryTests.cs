using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers <see cref="TrackPieceRef.DisplaySummaryWithoutComposer"/> and the
/// <see cref="AlbumTrack.DisplaySummaryWithoutComposer"/> rollup — the
/// composer-less title used by the player caption (which shows the composer
/// separately on its second line).
/// </summary>
public class TrackPieceRefDisplaySummaryTests
{
    [Fact]
    public void WithoutComposer_DropsComposerPrefix_KeepsPieceAndSubpath()
    {
        var refr = new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van",
            PieceTitle = "Piano Sonata No. 14",
            SubpiecePath = new List<string> { "Adagio sostenuto" },
        };

        Assert.Equal(
            "Beethoven, Ludwig van – Piano Sonata No. 14: Adagio sostenuto",
            refr.DisplaySummary);
        Assert.Equal(
            "Piano Sonata No. 14: Adagio sostenuto",
            refr.DisplaySummaryWithoutComposer);
    }

    [Fact]
    public void WithoutComposer_KeepsVersionDescription()
    {
        var refr = new TrackPieceRef
        {
            Composer = "Bach, Johann Sebastian",
            PieceTitle = "The Art of Fugue",
            VersionDescription = "arr. for string quartet",
        };

        Assert.Equal(
            "The Art of Fugue (arr. for string quartet)",
            refr.DisplaySummaryWithoutComposer);
    }

    [Fact]
    public void WithoutComposer_DisplayLabelOverride_ReturnedVerbatim()
    {
        var refr = new TrackPieceRef
        {
            Composer = "Mozart, Wolfgang Amadeus",
            PieceTitle = "Symphony No. 40",
            DisplayLabel = "Sinfonie g-moll (CD title)",
        };

        // A user-supplied label is the user's own wording — not decomposed.
        Assert.Equal("Sinfonie g-moll (CD title)", refr.DisplaySummaryWithoutComposer);
    }

    [Fact]
    public void Variants_AppendedAsBracketedSuffix()
    {
        var refr = new TrackPieceRef
        {
            Composer   = "Beethoven, Ludwig van",
            PieceTitle = "Violin Concerto",
            Variants   = new List<VariantReference>
            {
                new() { Id = 5, Description = "Kreisler cadenza" },
                new() { Id = 9, Description = "shortened ending" },
            },
        };

        Assert.Equal(
            "Beethoven, Ludwig van – Violin Concerto [Kreisler cadenza; shortened ending]",
            refr.DisplaySummary);
        Assert.Equal(
            "Violin Concerto [Kreisler cadenza; shortened ending]",
            refr.DisplaySummaryWithoutComposer);
    }

    [Fact]
    public void Variants_OnAMovement_SuffixFollowsSubpath()
    {
        var refr = new TrackPieceRef
        {
            Composer     = "Beethoven, Ludwig van",
            PieceTitle   = "Violin Concerto",
            SubpiecePath = new List<string> { "Rondo" },
            Variants     = new List<VariantReference> { new() { Id = 9, Description = "shortened ending" } },
        };

        Assert.Equal(
            "Violin Concerto: Rondo [shortened ending]",
            refr.DisplaySummaryWithoutComposer);
    }

    [Fact]
    public void Variants_DisplayLabelOverride_StaysVerbatim_NoSuffix()
    {
        // A user-supplied label owns the wording entirely; the variant suffix
        // is not appended on top of it.
        var refr = new TrackPieceRef
        {
            Composer     = "Mozart, Wolfgang Amadeus",
            PieceTitle   = "Symphony No. 40",
            DisplayLabel = "Sinfonie g-moll (CD title)",
            Variants     = new List<VariantReference> { new() { Id = 1, Description = "v" } },
        };

        Assert.Equal("Sinfonie g-moll (CD title)", refr.DisplaySummary);
    }

    [Fact]
    public void AlbumTrack_WithoutComposer_JoinsRefs_AndFallsBackToDescription()
    {
        var catalogued = new AlbumTrack
        {
            PieceRefs = new List<TrackPieceRef>
            {
                new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Egmont Overture" },
            },
        };
        Assert.Equal("Egmont Overture", catalogued.DisplaySummaryWithoutComposer);

        var uncatalogued = new AlbumTrack { Description = "Applause" };
        Assert.Equal("Applause", uncatalogued.DisplaySummaryWithoutComposer);
    }
}
