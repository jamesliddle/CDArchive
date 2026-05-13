using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

public class CatalogPreferenceSortTests
{
    private static CanonPiece MakePiece(params (string Cat, string Num)[] entries)
    {
        var piece = new CanonPiece
        {
            Composer = "Chopin, Frédéric",
            Form     = "Scherzo",
            Number   = 1,
            CatalogInfo = entries.Select(e => new CatalogInfo
            {
                Catalog       = e.Cat,
                CatalogNumber = e.Num,
            }).ToList()
        };
        return piece;
    }

    [Fact]
    public void NullOrEmptyPreference_IsNoOp()
    {
        var piece = MakePiece(("B.", "65"), ("Op.", "20"));

        piece.SortCatalogInfoByPreference(null);
        Assert.Equal("B.",  piece.CatalogInfo![0].Catalog);
        Assert.Equal("Op.", piece.CatalogInfo[1].Catalog);

        piece.SortCatalogInfoByPreference([]);
        Assert.Equal("B.",  piece.CatalogInfo[0].Catalog);
        Assert.Equal("Op.", piece.CatalogInfo[1].Catalog);
    }

    [Fact]
    public void PreferredPrefixMovesToFront()
    {
        var piece = MakePiece(("B.", "65"), ("Op.", "20"));

        piece.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Equal("Op.", piece.CatalogInfo![0].Catalog);
        Assert.Equal("20",  piece.CatalogInfo[0].CatalogNumber);
        Assert.Equal("B.",  piece.CatalogInfo[1].Catalog);
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        var piece = MakePiece(("b.", "65"), ("op.", "20"));

        piece.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Equal("op.", piece.CatalogInfo![0].Catalog);
        Assert.Equal("b.",  piece.CatalogInfo[1].Catalog);
    }

    [Fact]
    public void UnmatchedPrefixKeepsRelativeOrderAfterMatched()
    {
        // Preference says [Op., B.]. Piece has [KK., B., Op.] — neither KK. is
        // in the preference, so KK. goes last; Op. goes first (rank 0), B. next
        // (rank 1). Stable: KK. retains its original-after-B. position among
        // unmatched entries (it's the only unmatched one here).
        var piece = MakePiece(("KK.", "1"), ("B.", "65"), ("Op.", "20"));

        piece.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Equal("Op.", piece.CatalogInfo![0].Catalog);
        Assert.Equal("B.",  piece.CatalogInfo[1].Catalog);
        Assert.Equal("KK.", piece.CatalogInfo[2].Catalog);
    }

    [Fact]
    public void RecursesIntoSubpieces()
    {
        var parent = MakePiece(("B.", "1"), ("Op.", "10"));
        parent.Subpieces =
        [
            MakePiece(("B.", "2"), ("Op.", "11")),
            MakePiece(("Op.", "12"), ("B.", "3")),
        ];

        parent.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Equal("Op.", parent.CatalogInfo![0].Catalog);
        Assert.Equal("Op.", parent.Subpieces[0].CatalogInfo![0].Catalog);
        Assert.Equal("Op.", parent.Subpieces[1].CatalogInfo![0].Catalog);
    }

    [Fact]
    public void RecursesIntoVersionsAndVersionSubpieces()
    {
        var piece = MakePiece(("B.", "1"), ("Op.", "10"));
        piece.Versions =
        [
            new CanonPieceVersion
            {
                Description = "string-orchestra arrangement",
                CatalogInfo =
                [
                    new() { Catalog = "B.",  CatalogNumber = "20" },
                    new() { Catalog = "Op.", CatalogNumber = "30" },
                ],
                Subpieces = [ MakePiece(("B.", "40"), ("Op.", "50")) ],
            }
        ];

        piece.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Equal("Op.", piece.CatalogInfo![0].Catalog);
        Assert.Equal("Op.", piece.Versions[0].CatalogInfo![0].Catalog);
        Assert.Equal("Op.", piece.Versions[0].Subpieces![0].CatalogInfo![0].Catalog);
    }

    [Fact]
    public void SingleEntryList_Untouched()
    {
        var piece = MakePiece(("B.", "65"));
        piece.SortCatalogInfoByPreference(["Op.", "B."]);
        Assert.Single(piece.CatalogInfo!);
        Assert.Equal("B.", piece.CatalogInfo![0].Catalog);
    }

    [Fact]
    public void NullCatalogList_Untouched()
    {
        var piece = new CanonPiece { Composer = "X", Title = "Y" };
        piece.SortCatalogInfoByPreference(["Op."]); // must not throw
        Assert.Null(piece.CatalogInfo);
    }

    [Fact]
    public void DisplayTitleReflectsReorder()
    {
        // The explicit semantic guarantee of this feature: reordering CatalogInfo
        // changes which catalog leads in the DisplayTitle.
        var piece = new CanonPiece
        {
            Composer    = "Chopin, Frédéric",
            Form        = "Scherzo",
            Number      = 1,
            KeyTonality = "B",
            KeyMode     = "minor",
            CatalogInfo =
            [
                new() { Catalog = "B.",  CatalogNumber = "65" },
                new() { Catalog = "Op.", CatalogNumber = "20" },
            ],
        };
        Assert.Contains("B. 65",  piece.DisplayTitle);
        Assert.DoesNotContain("Op. 20", piece.DisplayTitle);

        piece.SortCatalogInfoByPreference(["Op.", "B."]);

        Assert.Contains("Op. 20", piece.DisplayTitle);
        Assert.DoesNotContain("B. 65", piece.DisplayTitle);
    }
}
