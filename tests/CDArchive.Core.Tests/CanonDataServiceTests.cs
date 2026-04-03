using System.Text.Json;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

public class CanonDataServiceTests
{
    private static string FindDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "Classical Canon composers.json")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find data/ directory");
    }

    [Fact]
    public async Task LoadComposers_DeserializesAll()
    {
        var service = new CanonDataService(FindDataDirectory());
        var composers = await service.LoadComposersAsync();
        Assert.True(composers.Count > 300, $"Expected 300+ composers, got {composers.Count}");
        Assert.All(composers, c => Assert.False(string.IsNullOrEmpty(c.Name)));
    }

    [Fact]
    public async Task LoadPieces_DeserializesAll()
    {
        var service = new CanonDataService(FindDataDirectory());
        var pieces = await service.LoadPiecesAsync();
        Assert.True(pieces.Count > 400, $"Expected 400+ pieces, got {pieces.Count}");
    }

    [Fact]
    public void DisplayTitle_DerivedFromForm()
    {
        var piece = new CanonPiece
        {
            Form = "piano sonata",
            Number = 4,
            KeyTonality = "E♭",
            KeyMode = "major",
            CatalogInfo = [new CatalogInfo { Catalog = "Op.", CatalogNumber = "7" }]
        };
        Assert.Equal("Piano Sonata #4 in E♭, Op. 7", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_SetInfersKind()
    {
        var piece = new CanonPiece
        {
            Form = "set",
            CatalogInfo = [new CatalogInfo { Catalog = "Op.", CatalogNumber = "1" }],
            Subpieces =
            [
                new CanonPiece { Form = "piano trio", Number = 1 },
                new CanonPiece { Form = "piano trio", Number = 2 },
                new CanonPiece { Form = "piano trio", Number = 3 },
            ]
        };
        Assert.Equal("Three Piano Trios, Op. 1", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_SetWithoutSubpieceForm()
    {
        var piece = new CanonPiece
        {
            Form = "set",
            CatalogInfo = [new CatalogInfo { Catalog = "Op.", CatalogNumber = "18" }],
            Subpieces =
            [
                new CanonPiece { Number = 1 },
                new CanonPiece { Number = 2 },
                new CanonPiece { Number = 3 },
                new CanonPiece { Number = 4 },
                new CanonPiece { Number = 5 },
                new CanonPiece { Number = 6 },
            ]
        };
        Assert.Equal("Six Pieces, Op. 18", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_ExplicitTitleUnchanged()
    {
        var piece = new CanonPiece { Title = "Ma M\u00e8re l'Oye" };
        Assert.Equal("Ma M\u00e8re l'Oye", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_SubpieceWithPropagatedCatalog()
    {
        // Simulates what happens after PropagateCatalogNumbers:
        // Parent Op. 2 → subpiece gets CatalogNumber="2", CatalogSubnumber="1"
        var piece = new CanonPiece
        {
            Form = "piano sonata",
            Number = 1,
            KeyTonality = "F",
            KeyMode = "minor",
            CatalogInfo = [new CatalogInfo { Catalog = "Op.", CatalogNumber = "2", CatalogSubnumber = "1" }]
        };
        Assert.Equal("Piano Sonata #1 in f, Op. 2 #1", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MajorKeyUppercase()
    {
        var piece = new CanonPiece
        {
            Form = "symphony",
            Number = 5,
            KeyTonality = "C",
            KeyMode = "minor",
            CatalogInfo = [new CatalogInfo { Catalog = "Op.", CatalogNumber = "67" }]
        };
        Assert.Equal("Symphony #5 in c, Op. 67", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementWithFormAndTempo()
    {
        var piece = new CanonPiece
        {
            Form = "Scherzo",
            Number = 3,
            Tempos = [new TempoInfo { Number = 1, Description = "Allegro assai" }]
        };
        Assert.Equal("3. Scherzo. Allegro assai", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementWithMultipleTempos()
    {
        var piece = new CanonPiece
        {
            Number = 1,
            Tempos =
            [
                new TempoInfo { Number = 1, Description = "Lent" },
                new TempoInfo { Number = 2, Description = "Allegro vivo" }
            ]
        };
        Assert.Equal("1. Lent - Allegro vivo", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementWithNestedTempos()
    {
        // Some movements have tempos nested inside tempos (no direct Description)
        var piece = new CanonPiece
        {
            Number = 1,
            Tempos =
            [
                new TempoInfo
                {
                    Number = 1,
                    SubTempos = [new TempoInfo { Number = 1, Description = "Adagio" }]
                },
                new TempoInfo
                {
                    Number = 2,
                    SubTempos = [new TempoInfo { Number = 1, Description = "Allegro vivace" }]
                }
            ]
        };
        Assert.Equal("1. Adagio - Allegro vivace", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementFormAndMultipleTempos()
    {
        var piece = new CanonPiece
        {
            Form = "Finale",
            Number = 4,
            Tempos =
            [
                new TempoInfo { Number = 1, Description = "Lent" },
                new TempoInfo { Number = 2, Description = "Allegro vivo" }
            ]
        };
        Assert.Equal("4. Finale. Lent - Allegro vivo", piece.DisplayTitle);
    }
}
