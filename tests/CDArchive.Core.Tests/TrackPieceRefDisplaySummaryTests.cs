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
