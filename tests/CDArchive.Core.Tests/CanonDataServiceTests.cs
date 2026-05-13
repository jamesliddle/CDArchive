using System.Text.Json;
using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

public class CanonDataServiceTests
{
    private static string FindDataDirectory()
    {
        // Mirror the dual-marker resolution in CanonDataService: the data
        // directory is identified by either composers.json or ClassicalCanon.db,
        // so the suite still finds data/ when JSON has been deleted post-migration.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate) &&
                (File.Exists(Path.Combine(candidate, "Classical Canon composers.json")) ||
                 File.Exists(Path.Combine(candidate, "ClassicalCanon.db"))))
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

    /// <summary>
    /// Regression: the parameterless <see cref="CanonDataService"/> ctor used to
    /// rely solely on <c>Classical Canon composers.json</c> as its directory marker,
    /// which meant deleting that JSON file would also break the SQLite path
    /// (because <see cref="ServiceCollectionExtensions"/> derives the DB connection
    /// string from <c>ComposersFilePath</c>). After the SQLite migration, the
    /// resolver also accepts <c>ClassicalCanon.db</c> as a marker, so the data
    /// directory remains discoverable when the JSON files have been deleted.
    /// </summary>
    [Fact]
    public void Constructor_ResolvesViaDatabaseMarker_WhenComposersJsonAbsent()
    {
        var dataDir       = FindDataDirectory();
        var composersJson = Path.Combine(dataDir, "Classical Canon composers.json");
        var dbFile        = Path.Combine(dataDir, "ClassicalCanon.db");

        // The marker only kicks in if the DB file exists. If a clean checkout
        // hasn't been seeded yet, skip — the resolver still falls back to JSON.
        if (!File.Exists(dbFile)) return;

        var bak = composersJson + ".test-resolver-bak";
        File.Move(composersJson, bak);
        try
        {
            var svc          = new CanonDataService();
            var resolvedDir  = Path.GetDirectoryName(svc.ComposersFilePath);
            Assert.Equal(dataDir, resolvedDir);
        }
        finally
        {
            // Restore aggressively so a test failure can't corrupt the data dir.
            if (File.Exists(bak)) File.Move(bak, composersJson, overwrite: true);
        }
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
            Markers = [Tempo("Allegro assai")],
        };
        Assert.Equal("3. Scherzo. Allegro assai", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementWithMultipleTempos()
    {
        var piece = new CanonPiece
        {
            Number = 1,
            Markers = [Tempo("Lent"), Tempo("Allegro vivo")],
        };
        Assert.Equal("1. Lent - Allegro vivo", piece.DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_MovementWithMultipleFlatTempos()
    {
        // Movements with two consecutive tempos in the same section (e.g.
        // a slow introduction followed by an Allegro main section, like
        // Beethoven Op. 1 No. 2 mvt. I) carry them as flat siblings in the
        // marker list.
        var piece = new CanonPiece
        {
            Number = 1,
            Markers = [Tempo("Adagio"), Tempo("Allegro vivace")],
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
            Markers = [Tempo("Lent"), Tempo("Allegro vivo")],
        };
        Assert.Equal("4. Finale. Lent - Allegro vivo", piece.DisplayTitle);
    }

    /// <summary>Test-fixture helper: a kind=Tempo marker with the given description.</summary>
    private static MusicalMarker Tempo(string description) =>
        new() { Kind = MarkerKind.Tempo, Value = description };
}
