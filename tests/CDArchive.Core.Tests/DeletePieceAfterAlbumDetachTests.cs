using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Replicates the user-reported scenario:
/// 1. Add a new piece.
/// 2. Add the piece to an album track.
/// 3. Delete the album track (album save should cascade-delete the track piece-ref).
/// 4. Delete the piece. The save reports success but the piece survives — both
///    in DB and after app restart.
/// </summary>
public class DeletePieceAfterAlbumDetachTests
{
    private static SqliteCanonDataService NewService(out IDbContextFactory<CanonDbContext> factory)
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_delete_after_detach_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    [Fact]
    public async Task ForeignKeysAreEnforced_OnEveryConnectionFromFactory()
    {
        // Sanity check that the connection-string FK option takes effect:
        // PRAGMA foreign_keys should return 1 on every connection the
        // DbContextFactory hands out.
        NewService(out var factory);
        for (int i = 0; i < 3; i++)
        {
            await using var db = await factory.CreateDbContextAsync();
            var result = await db.Database.SqlQueryRaw<long>("PRAGMA foreign_keys").ToListAsync();
            Assert.Equal(1L, result.Single());
        }
    }

    [Fact]
    public async Task SinglePersistedInstance_AddedToAlbum_DetachedFromAlbum_ThenDeleted()
    {
        // Same instance of the new piece used throughout — mimics what the
        // app does (vm.Pieces holds the same CanonPiece reference between
        // step 1's save and step 4's delete; CWT identity should resolve).
        var svc = NewService(out var factory);

        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });

        // Step 1: add the new piece in-memory. Save persists it. Same
        // instance stays in our list afterward (CWT now points to its row).
        var newPiece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Brand new piece",
        };
        var pieces = new List<CanonPiece> { newPiece };
        await svc.SavePiecesAsync(pieces);
        // newPiece object still in `pieces` — CWT was populated by the save.

        // Step 2: build an album track that points at the piece (by composer
        // + title strings, as the AlbumEditor does).
        var album = new CanonAlbum
        {
            Title = "Test album",
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
                                new() { Composer = newPiece.Composer!, PieceTitle = newPiece.Title! },
                            },
                        },
                    },
                },
            },
        };
        var albums = new List<CanonAlbum> { album };
        await svc.SaveAlbumsAsync(albums);

        // Step 3: remove the track from the same in-memory album instance,
        // save again.
        album.Discs[0].Tracks.Clear();
        await svc.SaveAlbumsAsync(albums);

        // Cross-check: no track piece-refs remain.
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.AlbumTrackPieceRefs.CountAsync());
        }

        // Step 4: delete the piece from the same in-memory pieces list (the
        // CanonViewModel does exactly this).
        pieces.Remove(newPiece);
        await svc.SavePiecesAsync(pieces);

        // Reload and check.
        var piecesAfter = await svc.LoadPiecesAsync();
        Assert.Empty(piecesAfter);
    }

    [Fact]
    public async Task FullScenario_JsonCloneAlbumEditor_AddPiece_LinkToAlbum_DetachAlbumTrack_DeletePiece()
    {
        // The AlbumEditor JSON-clones the input album before editing, so the
        // edited instance is a different CanonAlbum reference than the one
        // in vm.AllAlbums. The data service's CWT identity tracking misses
        // for the clone; the IdentityKey fallback path is what matches it to
        // the existing DB row. This is the exact path the user's bug followed.
        var svc = NewService(out var factory);

        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });

        var newPiece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Brand new piece",
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { newPiece });
        var savedPiece = (await svc.LoadPiecesAsync()).Single(p => p.Title == "Brand new piece");

        // Build the album from scratch (mimics user creating a fresh album +
        // adding the piece to a track).
        var freshAlbum = new CanonAlbum
        {
            Title = "Test album",
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
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { freshAlbum });

        // Simulate the AlbumEditor's flow: load albums, JSON-clone, mutate
        // clone (remove track), substitute clone back, save.
        var loadedAlbums = await svc.LoadAlbumsAsync();
        var loadedAlbum  = loadedAlbums.Single();

        var clonedJson = System.Text.Json.JsonSerializer.Serialize(loadedAlbum);
        var clone = System.Text.Json.JsonSerializer
            .Deserialize<CanonAlbum>(clonedJson)!;
        clone.Discs[0].Tracks.Clear();

        // Save with the clone in place of the original.
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { clone });

        // Verify the track piece-ref AND the track itself are gone.
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.AlbumTrackPieceRefs.CountAsync());
            Assert.Equal(0, await db.AlbumTracks.CountAsync());
        }

        // Now delete the piece — the user's step 4.
        var piecesBeforeDelete = await svc.LoadPiecesAsync();
        Assert.Single(piecesBeforeDelete);
        var remaining = piecesBeforeDelete
            .Where(p => p.Title != "Brand new piece")
            .ToList();
        await svc.SavePiecesAsync(remaining);

        var piecesAfter = await svc.LoadPiecesAsync();
        Assert.Empty(piecesAfter);
    }

    [Fact]
    public async Task FullScenario_AddPiece_LinkToAlbum_DetachAlbumTrack_DeletePiece()
    {
        var svc = NewService(out var factory);

        // Step 1: seed a composer + an existing album with one disc.
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });

        // Add a "new piece" — simulating the user's editor flow.
        var newPiece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Brand new piece",
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { newPiece });

        var loadedPieces = await svc.LoadPiecesAsync();
        var savedPiece = loadedPieces.Single(p => p.Title == "Brand new piece");

        // Step 2: add the piece to an album track. Album save resolves the
        // track piece-ref by composer + title.
        var album = new CanonAlbum
        {
            Title = "Test album",
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

        // Verify the track piece-ref reached the DB.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var refCount = await db.AlbumTrackPieceRefs.CountAsync();
            Assert.Equal(1, refCount);
        }

        // Step 3: simulate "deleted the album track" — load the album, remove
        // its only track, save.
        var loadedAlbums = await svc.LoadAlbumsAsync();
        var loadedAlbum = loadedAlbums.Single();
        loadedAlbum.Discs[0].Tracks.Clear();
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { loadedAlbum });

        // Verify the track piece-ref was cleaned up.
        await using (var db = await factory.CreateDbContextAsync())
        {
            var refCount = await db.AlbumTrackPieceRefs.CountAsync();
            Assert.Equal(0, refCount);
            var trackCount = await db.AlbumTracks.CountAsync();
            Assert.Equal(0, trackCount);
        }

        // Step 4: delete the piece. Simulate the Canon view's flow — load the
        // pieces, remove the new one from the input list, save.
        var piecesBeforeDelete = await svc.LoadPiecesAsync();
        Assert.Single(piecesBeforeDelete);
        var pieceToKeepCount = piecesBeforeDelete.Count(p => p.Title != "Brand new piece");

        // Pass the pieces list MINUS the deleted one (this is exactly what
        // DeletePieceAsync does).
        var remainingPieces = piecesBeforeDelete
            .Where(p => p.Title != "Brand new piece")
            .ToList();
        await svc.SavePiecesAsync(remainingPieces);

        // Step 5: reload and verify the piece is GONE.
        var piecesAfterDelete = await svc.LoadPiecesAsync();
        Assert.Equal(pieceToKeepCount, piecesAfterDelete.Count);
        Assert.DoesNotContain(piecesAfterDelete, p => p.Title == "Brand new piece");
    }
}
