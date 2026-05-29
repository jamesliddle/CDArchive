using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Regression for the user-reported bug: deleting a piece that's referenced
/// by an album's track silently succeeded — the in-memory deletion was
/// committed and "Deleted" status appeared, but the album still referenced
/// the now-orphaned piece_id.
///
/// <para>Root cause: <see cref="CanonDbContext"/>'s
/// <c>OnDelete(DeleteBehavior.Restrict)</c> on
/// <c>album_track_piece_refs.piece_id</c> is correctly configured at the
/// EF level. But the SQLite connection string omitted
/// <c>Foreign Keys=True</c>, which Microsoft.Data.Sqlite requires to
/// enable FK enforcement (it defaults OFF). Without enforcement, the
/// <c>SaveChangesAsync</c> on the delete pass succeeded silently and the
/// try/catch around it in <c>SavePiecesCoreAsync</c> never fired. The
/// fix turns FK enforcement on in both production and test connection
/// strings.</para>
/// </summary>
public class DeletePieceWithAlbumRefTests
{
    private static SqliteCanonDataService NewService(out IDbContextFactory<CanonDbContext> factory)
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_delete_piece_ref_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    [Fact]
    public async Task SavePieces_DeletingPieceReferencedByAlbumTrack_ThrowsInvalidOperation()
    {
        // Step 1: seed a composer, a piece, and an album whose track ref points
        // at the piece.
        var svc = NewService(out _);
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Symphony No. 9",
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var pieces = await svc.LoadPiecesAsync();
        var savedPiece = pieces.Single();

        var album = new CanonAlbum
        {
            Title = "Karajan / Beethoven 9",
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
                            PieceRefs   = new List<TrackPieceRef>
                            {
                                new()
                                {
                                    Composer   = savedPiece.Composer!,
                                    PieceTitle = savedPiece.Title!,
                                },
                            },
                        },
                    },
                },
            },
        };
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Step 2: simulate the user deleting the piece — pass an empty pieces
        // list (the deleted piece is now missing from the input).
        var loadedPieces = await svc.LoadPiecesAsync();
        Assert.Single(loadedPieces);                 // piece still there

        // The save should refuse because the album track ref still points
        // at it (FK Restrict). Pre-fix it succeeded silently.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await svc.SavePiecesAsync(new List<CanonPiece>());
        });

        // Step 3: verify the piece is still in the canon (rollback worked).
        var reloaded = await svc.LoadPiecesAsync();
        Assert.Single(reloaded);
        Assert.Equal("Symphony No. 9", reloaded[0].Title);
    }

    [Fact]
    public async Task SavePieces_DeletingUnreferencedPiece_Succeeds()
    {
        // Sanity check: deleting an orphan piece (no album refs) works fine.
        var svc = NewService(out _);
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });
        await svc.SavePiecesAsync(new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Symphony No. 9" },
        });

        // Delete (pass empty list).
        await svc.SavePiecesAsync(new List<CanonPiece>());

        var reloaded = await svc.LoadPiecesAsync();
        Assert.Empty(reloaded);
    }
}
