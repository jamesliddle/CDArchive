using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Sort-mode tests for the composer list. Mirrors the piece-sort coverage:
/// every supported field has a positive ordering test, default routing is
/// pinned, and the Recordings sort is exercised against a stub count delegate
/// so it can be unit-tested without a live PieceReferenceIndex.
/// </summary>
public class ComposerSortingTests
{
    private static CanonComposer C(string name, int? born = null, int? died = null,
                                   int pieceCount = 0)
    {
        return new CanonComposer
        {
            Name       = name,
            SortName   = name,
            BirthDate  = born?.ToString(),
            DeathDate  = died?.ToString(),
            PieceCount = pieceCount,
        };
    }

    [Fact]
    public void ParseField_LabelMappings_AndDefaultDirections()
    {
        Assert.Equal((ComposerSortField.Pieces,     false), ComposerSorting.ParseField("Pieces"));
        Assert.Equal((ComposerSortField.Recordings, false), ComposerSorting.ParseField("Recordings"));
        Assert.Equal((ComposerSortField.Name,       true),  ComposerSorting.ParseField("Name"));
        Assert.Equal((ComposerSortField.Birth,      true),  ComposerSorting.ParseField("Born"));
        Assert.Equal((ComposerSortField.Death,      true),  ComposerSorting.ParseField("Died"));
        // Unknown / null falls through to the XAML SelectedIndex=0 default.
        Assert.Equal((ComposerSortField.Pieces, false), ComposerSorting.ParseField(null));
        Assert.Equal((ComposerSortField.Pieces, false), ComposerSorting.ParseField(""));
        Assert.Equal((ComposerSortField.Pieces, false), ComposerSorting.ParseField("Bogus"));
    }

    [Fact]
    public void Pieces_DescendingByDefault_TieBreaksByName()
    {
        var beethoven = C("Beethoven, Ludwig van", pieceCount: 250);
        var bach      = C("Bach, Johann Sebastian", pieceCount: 250); // tie with Beethoven
        var ravel     = C("Ravel, Maurice", pieceCount: 70);

        var result = ComposerSorting.Sort(
            new[] { ravel, beethoven, bach },
            ComposerSortField.Pieces, ascending: false);

        // Most pieces first; tied at 250 → Bach < Beethoven by name.
        Assert.Equal(new[] { bach, beethoven, ravel }, result);
    }

    [Fact]
    public void Name_AscendingAlphabetical()
    {
        var bach   = C("Bach, Johann Sebastian");
        var ravel  = C("Ravel, Maurice");
        var schmitt = C("Schmitt, Florent");

        var result = ComposerSorting.Sort(
            new[] { schmitt, ravel, bach },
            ComposerSortField.Name, ascending: true);

        Assert.Equal(new[] { bach, ravel, schmitt }, result);
    }

    [Fact]
    public void Birth_AscendingByYear_TieBreaksByName()
    {
        var bach      = C("Bach, Johann Sebastian", born: 1685);
        var handel    = C("Handel, George Frideric", born: 1685); // same year
        var beethoven = C("Beethoven, Ludwig van", born: 1770);

        var result = ComposerSorting.Sort(
            new[] { beethoven, handel, bach },
            ComposerSortField.Birth, ascending: true);

        Assert.Equal(new[] { bach, handel, beethoven }, result);
    }

    [Fact]
    public void Recordings_DescendingByDefault_TieBreaksByName()
    {
        var beethoven = C("Beethoven, Ludwig van");
        var ravel     = C("Ravel, Maurice");
        var schmitt   = C("Schmitt, Florent");
        var counts = new Dictionary<string, int>
        {
            ["Beethoven, Ludwig van"] = 850,
            ["Ravel, Maurice"]        = 200,
            ["Schmitt, Florent"]      = 1,
        };

        var result = ComposerSorting.Sort(
            new[] { ravel, schmitt, beethoven },
            ComposerSortField.Recordings, ascending: false,
            recordingCount: c => counts[c.Name]);

        Assert.Equal(new[] { beethoven, ravel, schmitt }, result);
    }

    [Fact]
    public void Recordings_NullDelegate_DegradesToNameOrder()
    {
        // No PieceReferenceIndex available → all counts collapse to 0, and
        // the tie-break fallback (SortName ascending) takes over.
        var beethoven = C("Beethoven, Ludwig van");
        var ravel     = C("Ravel, Maurice");

        var result = ComposerSorting.Sort(
            new[] { ravel, beethoven },
            ComposerSortField.Recordings, ascending: false,
            recordingCount: null);

        Assert.Equal(new[] { beethoven, ravel }, result);
    }

    [Fact]
    public void Recordings_TiesBreakByName_NotByPieceCount()
    {
        // Two composers with equal recording counts but different piece
        // counts — the recording sort must still tie-break alphabetically,
        // not by piece count. (PieceCount is the Pieces-sort field; mixing
        // the two would surprise the user.)
        var beethoven = C("Beethoven, Ludwig van", pieceCount: 250);
        var ravel     = C("Ravel, Maurice",        pieceCount: 70);
        var counts    = new Dictionary<string, int>
        {
            ["Beethoven, Ludwig van"] = 5,
            ["Ravel, Maurice"]        = 5,
        };

        var result = ComposerSorting.Sort(
            new[] { ravel, beethoven },
            ComposerSortField.Recordings, ascending: false,
            recordingCount: c => counts[c.Name]);

        Assert.Equal(new[] { beethoven, ravel }, result);
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(ComposerSorting.Sort(Array.Empty<CanonComposer>(),
            ComposerSortField.Pieces,     ascending: false));
        Assert.Empty(ComposerSorting.Sort(Array.Empty<CanonComposer>(),
            ComposerSortField.Recordings, ascending: false));
    }
}
