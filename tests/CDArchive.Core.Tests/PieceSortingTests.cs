using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Sort-mode tests for the piece list under each composer in the Canon view.
/// Locks in the four supported orderings (Catalogue / Title / Category / Year)
/// against the <see cref="PieceSorting.Sort"/> helper. Regression coverage
/// for the bug where <c>ComposerTreeNode.AllItems</c> silently re-sorted by
/// title and overrode whichever field the user picked.
/// </summary>
public class PieceSortingTests
{
    /// <summary>Builds a piece with a single CatalogInfo entry.</summary>
    private static CanonPiece P(string title, string? catPrefix = null,
                                 string? catNumber = null, string? category = null,
                                 int? year = null) => new()
    {
        Title       = title,
        CatalogInfo = catPrefix is null && catNumber is null
            ? null
            : [new CatalogInfo
            {
                Catalog       = catPrefix       ?? "",
                CatalogNumber = catNumber,
            }],
        InstrumentationCategory = category,
        PublicationYear         = year,
    };

    [Fact]
    public void ParseField_Default_IsCatalogue()
    {
        // Confirms the default routing: any unknown / null sort label falls
        // back to Catalogue, matching the XAML SelectedIndex=0 entry.
        Assert.Equal(PieceSortField.Catalogue, PieceSorting.ParseField(null));
        Assert.Equal(PieceSortField.Catalogue, PieceSorting.ParseField(""));
        Assert.Equal(PieceSortField.Catalogue, PieceSorting.ParseField("Catalogue"));
        Assert.Equal(PieceSortField.Catalogue, PieceSorting.ParseField("Bogus"));
        Assert.Equal(PieceSortField.Title,     PieceSorting.ParseField("Title"));
        Assert.Equal(PieceSortField.Category,  PieceSorting.ParseField("Category"));
        Assert.Equal(PieceSortField.Year,      PieceSorting.ParseField("Year"));
    }

    [Fact]
    public void Catalogue_OrdersByPrefixThenNumber()
    {
        // Op. 2 should precede Op. 10 (numeric, not lexicographic) and both
        // should precede WoO. 47.
        var op2     = P("Sonata #1",      catPrefix: "Op.",  catNumber: "2");
        var op10    = P("Sonata #5",      catPrefix: "Op.",  catNumber: "10");
        var op2_3   = P("Sonata #3",      catPrefix: "Op.",  catNumber: "2"); // tie: prefix+number same
        var woo47   = P("Easy Sonata",    catPrefix: "WoO.", catNumber: "47");

        var result = PieceSorting.Sort(new[] { woo47, op10, op2_3, op2 }, null,
                                       PieceSortField.Catalogue);

        // Op. 2 #1 < Op. 2 #3 < Op. 10 #5 < WoO. 47.
        Assert.Equal(new object[] { op2, op2_3, op10, woo47 }, result);
    }

    [Fact]
    public void Catalogue_PieceWithoutCatalog_SortsLast()
    {
        // No-catalogue pieces are pushed to the end (CatalogSortPrefix = U+FFFF
        // sorts after every real prefix).
        var op1     = P("Trio",       catPrefix: "Op.", catNumber: "1");
        var noCat   = P("Anhang");

        var result = PieceSorting.Sort(new[] { noCat, op1 }, null,
                                       PieceSortField.Catalogue);

        Assert.Equal(new object[] { op1, noCat }, result);
    }

    [Fact]
    public void Title_OrdersAlphabetically()
    {
        var b = P("B Major Sonata");
        var a = P("A Minor Concerto");
        var c = P("C Major Variations");

        var result = PieceSorting.Sort(new[] { b, c, a }, null, PieceSortField.Title);

        Assert.Equal(new object[] { a, b, c }, result);
    }

    [Fact]
    public void Year_OrdersByPublicationYear_NullsLast()
    {
        var p1799 = P("Early",  year: 1799);
        var p1827 = P("Late",   year: 1827);
        var p1815 = P("Mid",    year: 1815);
        var pNull = P("Unknown");

        var result = PieceSorting.Sort(new[] { p1815, pNull, p1827, p1799 }, null,
                                       PieceSortField.Year);

        Assert.Equal(new object[] { p1799, p1815, p1827, pNull }, result);
    }

    [Fact]
    public void Category_OrdersAlphabetically_TiesBreakByCatalogue()
    {
        var chamber  = P("String Quartet", catPrefix: "Op.", catNumber: "18", category: "Chamber");
        var orch     = P("Symphony #1",    catPrefix: "Op.", catNumber: "21", category: "Orchestra");
        var piano1   = P("Sonata #5",      catPrefix: "Op.", catNumber: "10", category: "Piano");
        var piano2   = P("Sonata #1",      catPrefix: "Op.", catNumber: "2",  category: "Piano");

        var result = PieceSorting.Sort(new[] { orch, piano1, chamber, piano2 }, null,
                                       PieceSortField.Category);

        // Chamber < Orchestra < Piano (alphabetical), Piano ties broken by Op. 2 < Op. 10.
        Assert.Equal(new object[] { chamber, orch, piano2, piano1 }, result);
    }

    [Fact]
    public void Catalogue_TiebreaksByTitle_WhenCatalogueIdentical()
    {
        // Two pieces with identical CatalogInfo (prefix+number+suffix all match)
        // — tie-break is on title.
        var b = P("B Aria",    catPrefix: "WoO.", catNumber: "1");
        var a = P("A Aria",    catPrefix: "WoO.", catNumber: "1");

        var result = PieceSorting.Sort(new[] { b, a }, null, PieceSortField.Catalogue);

        Assert.Equal(new object[] { a, b }, result);
    }

    [Fact]
    public void CrossComposerNodes_InterleaveByCatalogue_UsingParentTopPiece()
    {
        // Ravel has his own piano works AND a contributed movement of
        // L'éventail de Jeanne (whose top piece carries no opus). The
        // cross-composer node sorts by its TopPiece's catalogue keys —
        // null catalogue → after Op.-numbered works.
        var bolero    = P("Boléro",       catPrefix: "M.", catNumber: "81");
        var leventail = P("L'éventail de Jeanne");                  // no catalogue
        var fanfare   = new CanonPiece { Title = "Fanfare", Number = 1 };
        leventail.Subpieces = [fanfare];
        var fanfareNode = new CrossComposerSubpieceNode(
            topPiece: leventail,
            ancestorPath: new[] { leventail },
            subpiece: fanfare,
            parentNumberedSubpieces: true);

        var result = PieceSorting.Sort(
            new[] { bolero },
            new[] { fanfareNode },
            PieceSortField.Catalogue);

        // Boléro (M. 81) sorts before the fanfare (parent has no catalogue → sort-end).
        Assert.Equal(2, result.Count);
        Assert.Same(bolero,      result[0]);
        Assert.Same(fanfareNode, result[1]);
    }

    [Fact]
    public void CrossComposerNodes_InterleaveByTitle()
    {
        // For Title sort the cross-composer node uses its composite display
        // title ("L'éventail de Jeanne - 1. Fanfare") — sorts among the L's.
        var arabesque = P("Arabesque");                   // 'A'
        var sonatine  = P("Sonatine");                    // 'S'
        var leventail = P("L'éventail de Jeanne");        // 'L' — for title comparison
        var fanfare   = new CanonPiece { Title = "Fanfare", Number = 1 };
        leventail.Subpieces = [fanfare];
        var fanfareNode = new CrossComposerSubpieceNode(
            topPiece: leventail,
            ancestorPath: new[] { leventail },
            subpiece: fanfare,
            parentNumberedSubpieces: true);

        var result = PieceSorting.Sort(
            new[] { sonatine, arabesque },
            new[] { fanfareNode },
            PieceSortField.Title);

        // Display title order: "Arabesque" < "L'éventail de Jeanne - 1. Fanfare" < "Sonatine".
        Assert.Equal(3, result.Count);
        Assert.Same(arabesque,   result[0]);
        Assert.Same(fanfareNode, result[1]);
        Assert.Same(sonatine,    result[2]);
    }

    [Fact]
    public void EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(PieceSorting.Sort(Array.Empty<CanonPiece>(), null, PieceSortField.Catalogue));
        Assert.Empty(PieceSorting.Sort(Array.Empty<CanonPiece>(), null, PieceSortField.Title));
        Assert.Empty(PieceSorting.Sort(Array.Empty<CanonPiece>(), null, PieceSortField.Category));
        Assert.Empty(PieceSorting.Sort(Array.Empty<CanonPiece>(), null, PieceSortField.Year));
        Assert.Empty(PieceSorting.Sort(Array.Empty<CanonPiece>(), null, PieceSortField.Recordings));
    }

    [Fact]
    public void Recordings_OrdersByCount_DescendingThenCatalogue()
    {
        // Three pieces with distinct hit counts — most-recorded should land
        // first; ties (none here) would fall back to catalogue, then title.
        var symphony5 = P("Symphony No. 5", catPrefix: "Op.", catNumber: "67");
        var symphony9 = P("Symphony No. 9", catPrefix: "Op.", catNumber: "125");
        var ww1       = P("Wellingtons Sieg", catPrefix: "Op.", catNumber: "91");
        var counts = new Dictionary<CanonPiece, int>
        {
            [symphony5] = 25,
            [symphony9] = 32,
            [ww1]       = 1,
        };

        var result = PieceSorting.Sort(
            new[] { ww1, symphony5, symphony9 }, null,
            PieceSortField.Recordings,
            recordingCount: o => counts[(CanonPiece)o]);

        Assert.Equal(new object[] { symphony9, symphony5, ww1 }, result);
    }

    [Fact]
    public void Recordings_TiesBreakByCatalogueThenTitle()
    {
        // Equal hit counts — tiebreaker is catalogue (Op. 2 before Op. 10),
        // then title within identical catalogue.
        var op2  = P("Sonata #1", catPrefix: "Op.", catNumber: "2");
        var op10 = P("Sonata #5", catPrefix: "Op.", catNumber: "10");
        var op2_b = P("Sonata #3", catPrefix: "Op.", catNumber: "2");
        var counts = new Dictionary<CanonPiece, int> { [op2] = 5, [op10] = 5, [op2_b] = 5 };

        var result = PieceSorting.Sort(
            new[] { op10, op2_b, op2 }, null,
            PieceSortField.Recordings,
            recordingCount: o => counts[(CanonPiece)o]);

        // All same count → cat sort wins: Op. 2 #1 < Op. 2 #3 < Op. 10 #5.
        Assert.Equal(new object[] { op2, op2_b, op10 }, result);
    }

    [Fact]
    public void Recordings_NullDelegate_FallsBackToCatalogueOrder()
    {
        // No PieceReferenceIndex available (e.g. data still loading) →
        // sort degrades to a stable catalogue-order view rather than blowing
        // up. Every item gets count 0 from the internal default delegate.
        var op2  = P("Sonata #1", catPrefix: "Op.", catNumber: "2");
        var op10 = P("Sonata #5", catPrefix: "Op.", catNumber: "10");

        var result = PieceSorting.Sort(
            new[] { op10, op2 }, null,
            PieceSortField.Recordings,
            recordingCount: null);

        Assert.Equal(new object[] { op2, op10 }, result);
    }

    [Fact]
    public void Recordings_CrossComposerNodes_UseSubpieceCount()
    {
        // The runtime adapter looks up cross-composer node hits via the
        // wrapped Subpiece — so passing a per-Subpiece count here reflects
        // the production routing. Owned piece has fewer hits → cross-credit
        // sorts first.
        var bolero    = P("Boléro", catPrefix: "M.", catNumber: "81");
        var leventail = P("L'éventail de Jeanne");
        var fanfare   = new CanonPiece { Title = "Fanfare", Number = 1 };
        leventail.Subpieces = [fanfare];
        var fanfareNode = new CrossComposerSubpieceNode(
            topPiece: leventail,
            ancestorPath: new[] { leventail },
            subpiece: fanfare,
            parentNumberedSubpieces: true);

        var result = PieceSorting.Sort(
            new[] { bolero },
            new[] { fanfareNode },
            PieceSortField.Recordings,
            recordingCount: o => o switch
            {
                CanonPiece p when p == bolero    => 12,
                CrossComposerSubpieceNode ccn    => 30, // Fanfare is on more recordings
                _                                => 0,
            });

        Assert.Equal(2, result.Count);
        Assert.Same(fanfareNode, result[0]);
        Assert.Same(bolero,      result[1]);
    }
}
