using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Slice 2 of feature/variants: album-track refs can identify which variant(s)
/// a recording uses, and which version. Covers the round-trip through SQLite
/// (the new <c>album_track_piece_ref_variants</c> join table + <c>version_id</c>
/// on the ref row), the available-variants collector, the migration, the
/// orphan-removal on de-selection, and the variant-delete diagnostic.
/// </summary>
public class AlbumTrackRefVariantTests
{
    private static SqliteCanonDataService NewService() => NewServiceAt(NewDbPath());

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"cdarchive_refvariant_{Guid.NewGuid():N}.db");

    private static SqliteCanonDataService NewServiceAt(string dbPath)
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    /// <summary>
    /// Seeds Beethoven + a "Violin Concerto" carrying:
    ///  - two top-level (whole-piece) variants: a cadenza choice;
    ///  - a "Rondo" movement with its own variant: an ending choice;
    ///  - an "arr. for piano" version with its own variant.
    /// Returns the service plus the loaded piece (with stable ids populated).
    /// </summary>
    private static async Task<(SqliteCanonDataService svc, CanonPiece piece)> SeedConcertoAsync()
    {
        var svc = NewService();
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

        var concerto = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo>
            {
                new() { Description = "Kreisler cadenza" },
                new() { Description = "Joachim cadenza" },
            },
            Subpieces = new List<CanonPiece>
            {
                new()
                {
                    Composer = "Beethoven, Ludwig van",
                    Title    = "Rondo",
                    Variants = new List<VariantInfo>
                    {
                        new() { Description = "shortened ending" },
                    },
                },
            },
            Versions = new List<CanonPieceVersion>
            {
                new()
                {
                    Description = "arr. for piano",
                    Variants    = new List<VariantInfo>
                    {
                        new() { Description = "with extended coda" },
                    },
                },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { concerto });

        var loaded = (await svc.LoadPiecesAsync()).Single();
        return (svc, loaded);
    }

    private static CanonAlbum BuildAlbumWithRef(string title, TrackPieceRef pieceRef) => new()
    {
        Title = title,
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
                        PieceRefs   = new List<TrackPieceRef> { pieceRef },
                    },
                },
            },
        },
    };

    private static TrackPieceRef RefFor(CanonAlbum album) =>
        album.Discs[0].Tracks[0].PieceRefs!.Single();

    [Fact]
    public async Task RefVariants_WholePiece_RoundTripThroughSqlite()
    {
        var (svc, piece) = await SeedConcertoAsync();
        var kreisler = piece.Variants!.Single(v => v.Description == "Kreisler cadenza");
        var joachim  = piece.Variants!.Single(v => v.Description == "Joachim cadenza");

        var album = BuildAlbumWithRef("VC recording", new TrackPieceRef
        {
            Composer   = "Beethoven, Ludwig van",
            PieceTitle = "Violin Concerto",
            Variants   = new List<VariantReference>
            {
                // Identify by stable id (the editor's normal path).
                new() { Id = joachim.Id },
                new() { Id = kreisler.Id },
            },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loadedRef = RefFor((await svc.LoadAlbumsAsync()).Single());
        Assert.NotNull(loadedRef.Variants);
        Assert.Equal(2, loadedRef.Variants!.Count);
        // Order preserved (Joachim was first in the selection).
        Assert.Equal(joachim.Id,  loadedRef.Variants[0].Id);
        Assert.Equal("Joachim cadenza", loadedRef.Variants[0].Description);
        Assert.Equal(kreisler.Id, loadedRef.Variants[1].Id);
        Assert.Equal("Kreisler cadenza", loadedRef.Variants[1].Description);
    }

    [Fact]
    public async Task RefVariants_OnMovement_LeafAndAncestorBothSelectable()
    {
        var (svc, piece) = await SeedConcertoAsync();
        var kreisler = piece.Variants!.Single(v => v.Description == "Kreisler cadenza");
        var rondo    = piece.Subpieces!.Single(s => s.Title == "Rondo");
        var ending   = rondo.Variants!.Single();

        // A track ref'ing the Rondo movement picks both a piece-level cadenza
        // (ancestor variant) and the movement's own ending variant (leaf).
        var album = BuildAlbumWithRef("VC Rondo", new TrackPieceRef
        {
            Composer     = "Beethoven, Ludwig van",
            PieceTitle   = "Violin Concerto",
            SubpiecePath = new List<string> { "Rondo" },
            Variants     = new List<VariantReference>
            {
                new() { Id = kreisler.Id },
                new() { Id = ending.Id },
            },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loadedRef = RefFor((await svc.LoadAlbumsAsync()).Single());
        Assert.NotNull(loadedRef.Variants);
        Assert.Equal(2, loadedRef.Variants!.Count);
        Assert.Contains(loadedRef.Variants!, v => v.Id == kreisler.Id && v.Description == "Kreisler cadenza");
        Assert.Contains(loadedRef.Variants!, v => v.Id == ending.Id   && v.Description == "shortened ending");
    }

    [Fact]
    public async Task RefVersion_AndItsVariant_RoundTrip()
    {
        var (svc, piece) = await SeedConcertoAsync();
        var version = piece.Versions!.Single();
        var coda    = version.Variants!.Single();

        var album = BuildAlbumWithRef("VC piano arr.", new TrackPieceRef
        {
            Composer           = "Beethoven, Ludwig van",
            PieceTitle         = "Violin Concerto",
            VersionId          = version.Id,
            VersionDescription = "arr. for piano",
            Variants           = new List<VariantReference> { new() { Id = coda.Id } },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loadedRef = RefFor((await svc.LoadAlbumsAsync()).Single());
        Assert.Equal(version.Id, loadedRef.VersionId);
        Assert.Equal("arr. for piano", loadedRef.VersionDescription);
        Assert.NotNull(loadedRef.Variants);
        Assert.Equal(coda.Id, loadedRef.Variants!.Single().Id);
        Assert.Equal("with extended coda", loadedRef.Variants!.Single().Description);
    }

    [Fact]
    public async Task RefVariants_ResolveByDescription_WhenIdAbsent()
    {
        // Mirrors the post-reseed / JSON-import path: the ref carries only the
        // description, no id. Resolution must still bind it to the right variant.
        var (svc, _) = await SeedConcertoAsync();

        var album = BuildAlbumWithRef("VC by desc", new TrackPieceRef
        {
            Composer   = "Beethoven, Ludwig van",
            PieceTitle = "Violin Concerto",
            Variants   = new List<VariantReference> { new() { Description = "Kreisler cadenza" } },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loadedRef = RefFor((await svc.LoadAlbumsAsync()).Single());
        Assert.NotNull(loadedRef.Variants);
        Assert.Equal("Kreisler cadenza", loadedRef.Variants!.Single().Description);
        Assert.NotEqual(0L, loadedRef.Variants!.Single().Id);
    }

    [Fact]
    public async Task DeselectingVariant_RemovesJoinRow_KeepsVariantDefinition()
    {
        var (svc, piece) = await SeedConcertoAsync();
        var kreisler = piece.Variants!.Single(v => v.Description == "Kreisler cadenza");

        var album = BuildAlbumWithRef("VC toggle", new TrackPieceRef
        {
            Composer   = "Beethoven, Ludwig van",
            PieceTitle = "Violin Concerto",
            Variants   = new List<VariantReference> { new() { Id = kreisler.Id } },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Reload, clear the selection, re-save.
        var reloaded = (await svc.LoadAlbumsAsync()).Single();
        RefFor(reloaded).Variants = null;
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { reloaded });

        var afterRef = RefFor((await svc.LoadAlbumsAsync()).Single());
        Assert.True(afterRef.Variants is null or { Count: 0 });

        // The variant definition itself survives on the piece.
        var pieceAfter = (await svc.LoadPiecesAsync()).Single();
        Assert.Contains(pieceAfter.Variants!, v => v.Description == "Kreisler cadenza");
    }

    [Fact]
    public async Task DeletingReferencedVariant_SurfacesFriendlyDiagnostic()
    {
        var (svc, piece) = await SeedConcertoAsync();
        var kreisler = piece.Variants!.Single(v => v.Description == "Kreisler cadenza");

        var album = BuildAlbumWithRef("VC ref", new TrackPieceRef
        {
            Composer   = "Beethoven, Ludwig van",
            PieceTitle = "Violin Concerto",
            Variants   = new List<VariantReference> { new() { Id = kreisler.Id } },
        });
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Remove the referenced variant from the piece and try to save.
        var pieceForEdit = (await svc.LoadPiecesAsync()).Single();
        pieceForEdit.Variants = pieceForEdit.Variants!
            .Where(v => v.Description != "Kreisler cadenza").ToList();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SavePiecesAsync(new List<CanonPiece> { pieceForEdit }));
        Assert.Contains("variant", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kreisler cadenza", ex.Message);

        // An unreferenced variant CAN still be removed (sanity: the block is
        // specific to the referenced one).
        var pieceAfter = (await svc.LoadPiecesAsync()).Single();
        Assert.Contains(pieceAfter.Variants!, v => v.Description == "Kreisler cadenza");
    }

    [Fact]
    public async Task CollectAvailableVariants_LeafAncestorAndVersion()
    {
        var (_, piece) = await SeedConcertoAsync();
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(new List<CanonPiece> { piece });

        // Whole piece → only the two cadenza variants.
        var wholePiece = resolver.CollectAvailableVariants(new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto",
        });
        Assert.Equal(2, wholePiece.Count);
        Assert.Contains(wholePiece, v => v.Description == "Kreisler cadenza");
        Assert.Contains(wholePiece, v => v.Description == "Joachim cadenza");

        // Movement → ancestor (cadenza) variants PLUS the movement's own.
        var movement = resolver.CollectAvailableVariants(new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto",
            SubpiecePath = new List<string> { "Rondo" },
        });
        Assert.Equal(3, movement.Count);
        Assert.Contains(movement, v => v.Description == "shortened ending");
        Assert.Contains(movement, v => v.Description == "Kreisler cadenza");

        // Version → top piece variants PLUS the version's own.
        var version = resolver.CollectAvailableVariants(new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto",
            VersionDescription = "arr. for piano",
        });
        Assert.Contains(version, v => v.Description == "with extended coda");
        Assert.Contains(version, v => v.Description == "Kreisler cadenza");
    }

    [Fact]
    public async Task NeedsVariantIdentification_TrueWhenAvailableButUnchosen()
    {
        var (_, piece) = await SeedConcertoAsync();
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(new List<CanonPiece> { piece });

        // Whole piece HAS variants, none chosen → needs identification.
        var unchosen = new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto",
        };
        Assert.True(resolver.NeedsVariantIdentification(unchosen));

        // Same ref, a variant chosen → no longer needs it.
        var chosen = new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto",
            Variants = new List<VariantReference> { new() { Description = "Kreisler cadenza" } },
        };
        Assert.False(resolver.NeedsVariantIdentification(chosen));
    }

    [Fact]
    public async Task NeedsVariantIdentification_FalseWhenPathHasNoVariants()
    {
        var svc = NewService();
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });
        await svc.SavePiecesAsync(new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Egmont Overture" },
        });
        var piece = (await svc.LoadPiecesAsync()).Single();

        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(new List<CanonPiece> { piece });

        Assert.False(resolver.NeedsVariantIdentification(new TrackPieceRef
        {
            Composer = "Beethoven, Ludwig van", PieceTitle = "Egmont Overture",
        }));
    }

    [Fact]
    public async Task JoinTable_Exists_AfterInit_AndReinitIsIdempotent()
    {
        var dbPath = NewDbPath();
        var svc1 = NewServiceAt(dbPath);
        // Force schema creation + migrations.
        await svc1.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

        await using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='album_track_piece_ref_variants'";
            Assert.NotNull(await cmd.ExecuteScalarAsync());
        }

        // A second service on the same file re-runs ApplySchemaUpgradesAsync;
        // the CREATE TABLE IF NOT EXISTS path must be a clean no-op.
        var svc2 = NewServiceAt(dbPath);
        var composers = await svc2.LoadComposersAsync();
        Assert.Single(composers);

        SqliteConnection.ClearAllPools();
    }
}
