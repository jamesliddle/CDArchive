using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Regression tests for <see cref="ICanonDataService.SaveBatchAsync"/>. The
/// retired-pre-batch behaviour chained four separate <c>Save*Async</c> calls
/// in <see cref="ICanonDataService"/> from <c>ItunesImportViewModel</c> and
/// <c>TracksViewModel</c>; a mid-sequence failure (e.g. a unique-constraint
/// violation on <c>album_tracks(disc_id, track_number)</c> after composers
/// and pieces had already persisted) left the canon partially written with no
/// way back. The new SaveBatchAsync runs every staged subsystem inside one
/// shared <c>BeginTransactionAsync</c> so a failure rolls them all back.
/// </summary>
public class SaveBatchAtomicityTests
{
    private static SqliteCanonDataService NewService(out IDbContextFactory<CanonDbContext> factory)
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_savebatch_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    private static CanonComposer Composer(string name) => new() { Name = name, SortName = name };

    private static CanonPiece Piece(string composer, string title) => new()
    {
        Composer = composer,
        Title    = title,
    };

    private static CanonAlbum AlbumWithSingleTrack(string title) => new()
    {
        Title = title,
        Discs = new List<AlbumDisc>
        {
            new()
            {
                DiscNumber = 1,
                Tracks = new List<AlbumTrack>
                {
                    new() { TrackNumber = 1, Description = "track 1" },
                },
            },
        },
    };

    /// <summary>
    /// Hits a unique-constraint violation on <c>album_tracks(disc_id, track_number)</c>
    /// — two tracks numbered #1 on the same disc — guaranteed to fail when
    /// <c>SaveAlbumsCoreAsync</c> calls <c>SaveChangesAsync</c>.
    /// </summary>
    private static CanonAlbum AlbumWithDuplicateTrackNumbers(string title) => new()
    {
        Title = title,
        Discs = new List<AlbumDisc>
        {
            new()
            {
                DiscNumber = 1,
                Tracks = new List<AlbumTrack>
                {
                    new() { TrackNumber = 1, Description = "track 1" },
                    new() { TrackNumber = 1, Description = "duplicate" },
                },
            },
        },
    };

    private static AlbumTrack LooseTrack(string description) => new()
    {
        TrackNumber = 0,
        Description = description,
    };

    [Fact]
    public async Task SaveBatchAsync_PersistsAllFourSubsystems()
    {
        var svc = NewService(out _);

        var composers   = new List<CanonComposer>  { Composer("Beethoven, Ludwig van") };
        var pieces      = new List<CanonPiece>     { Piece("Beethoven, Ludwig van", "Symphony No. 9") };
        var albums      = new List<CanonAlbum>     { AlbumWithSingleTrack("Karajan / DG") };
        var looseTracks = new List<AlbumTrack>     { LooseTrack("ad-hoc single") };

        await svc.SaveBatchAsync(composers, pieces, albums, looseTracks);

        Assert.Single(await svc.LoadComposersAsync());
        Assert.Single(await svc.LoadPiecesAsync());
        Assert.Single(await svc.LoadAlbumsAsync());
        Assert.Single(await svc.LoadLooseTracksAsync());
    }

    [Fact]
    public async Task SaveBatchAsync_RollsBackEverything_WhenAlbumsSaveViolatesConstraint()
    {
        // Seed a baseline state so we can verify the failed batch didn't
        // overwrite or erase it.
        var svc = NewService(out var factory);
        await svc.SaveBatchAsync(
            composers:   new List<CanonComposer> { Composer("Bach, Johann Sebastian") },
            pieces:      new List<CanonPiece>    { Piece("Bach, Johann Sebastian", "Goldberg Variations") },
            albums:      new List<CanonAlbum>    { AlbumWithSingleTrack("Gould 1981") },
            looseTracks: new List<AlbumTrack>    { LooseTrack("baseline loose") });

        // Snapshot the row id of the existing album-track so we can prove the
        // failed batch didn't even touch it (rolled back, not recovered after
        // partial commit).
        long baselineAlbumTrackId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            baselineAlbumTrackId = await db.AlbumTracks
                .Where(t => t.DiscId != null)
                .Select(t => t.Id)
                .SingleAsync();
        }

        // Now attempt a SaveBatch where albums violates the unique constraint
        // on (disc_id, track_number). Two tracks both numbered #1 on disc 1
        // fail the index. The composers + pieces preceding it have already
        // been staged inside the shared transaction.
        var nextComposers = new List<CanonComposer>
        {
            Composer("Bach, Johann Sebastian"),
            Composer("Beethoven, Ludwig van"),     // new — would persist on partial commit
        };
        var nextPieces = new List<CanonPiece>
        {
            Piece("Bach, Johann Sebastian", "Goldberg Variations"),
            Piece("Beethoven, Ludwig van", "Symphony No. 5"),  // new — same
        };
        var nextAlbums = new List<CanonAlbum>
        {
            AlbumWithDuplicateTrackNumbers("Doomed album"),
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.SaveBatchAsync(nextComposers, nextPieces, nextAlbums));

        // After rollback: still exactly the baseline composer / piece /
        // album-track / loose track. The new "Beethoven" composer and
        // "Symphony No. 5" piece must NOT have leaked through.
        var composersAfter = await svc.LoadComposersAsync();
        Assert.Single(composersAfter);
        Assert.Equal("Bach, Johann Sebastian", composersAfter[0].Name);

        var piecesAfter = await svc.LoadPiecesAsync();
        Assert.Single(piecesAfter);
        Assert.Equal("Goldberg Variations", piecesAfter[0].Title);

        var albumsAfter = await svc.LoadAlbumsAsync();
        Assert.Single(albumsAfter);
        Assert.Equal("Gould 1981", albumsAfter[0].Title);

        var looseAfter = await svc.LoadLooseTracksAsync();
        Assert.Single(looseAfter);

        // Baseline album-track row id survived — proves the rollback was
        // genuine, not a "drop everything and re-seed" recovery.
        await using var db2 = await factory.CreateDbContextAsync();
        var baselineStillThere = await db2.AlbumTracks
            .Where(t => t.DiscId != null)
            .Select(t => t.Id)
            .SingleAsync();
        Assert.Equal(baselineAlbumTrackId, baselineStillThere);
    }

    [Fact]
    public async Task SaveBatchAsync_NullSubsetIsNoOp()
    {
        var svc = NewService(out _);
        // Calling with everything null should not throw and should not create
        // any rows (it's effectively a request to save nothing).
        await svc.SaveBatchAsync();
        Assert.Empty(await svc.LoadComposersAsync());
        Assert.Empty(await svc.LoadPiecesAsync());
    }

    [Fact]
    public async Task SaveBatchAsync_PartialSubset_SavesOnlySpecified()
    {
        var svc = NewService(out _);
        // Composers only — pieces, albums, loose tracks all stay empty.
        await svc.SaveBatchAsync(composers: new List<CanonComposer> { Composer("Mozart, Wolfgang Amadeus") });
        Assert.Single(await svc.LoadComposersAsync());
        Assert.Empty(await svc.LoadPiecesAsync());
        Assert.Empty(await svc.LoadAlbumsAsync());
        Assert.Empty(await svc.LoadLooseTracksAsync());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pick lists in SaveBatchAsync — retires Rework C14: PickListsViewModel
    // used to fire-and-forget the pieces save after applying renames. Now the
    // pick-lists + pieces saves go through SaveBatchAsync so either both land
    // or both roll back.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveBatchAsync_PickListsOnly_PersistsValues()
    {
        var svc = NewService(out _);

        var pl = new CanonPickLists
        {
            Forms        = new List<string> { "Symphony", "Sonata" },
            Categories   = new List<string> { "Orchestra", "Piano" },
        };

        await svc.SaveBatchAsync(pickLists: pl);

        var loaded = await svc.LoadPickListsAsync();
        // SavePickListsCoreAsync sorts the input in-place; assert the sorted
        // shape so we exercise the same serialise → deserialise pipeline the
        // app uses.
        Assert.Equal(new[] { "Sonata", "Symphony" }, loaded.Forms);
        Assert.Equal(new[] { "Orchestra", "Piano" }, loaded.Categories);
    }

    [Fact]
    public async Task SaveBatchAsync_PickListsAndPieces_BothLand()
    {
        var svc = NewService(out _);

        // Seed a composer + a piece whose Form is the pre-rename value.
        await svc.SaveBatchAsync(
            composers: new List<CanonComposer> { Composer("Beethoven, Ludwig van") });
        await svc.SaveBatchAsync(
            pieces: new List<CanonPiece>
            {
                new() { Composer = "Beethoven, Ludwig van", Title = "Sonata Op. 27 #2", Form = "Concertino" },
            });

        // Simulate the PickListsViewModel rename flow: rename the pick-list
        // value and update every affected piece, then SaveBatchAsync(pl,
        // pieces).
        var renamedPieces = (await svc.LoadPiecesAsync())
            .Select(p => { p.Form = "Concertino for Orchestra"; return p; })
            .ToList();
        var pl = new CanonPickLists
        {
            Forms = new List<string> { "Concertino for Orchestra" },
        };

        await svc.SaveBatchAsync(pickLists: pl, pieces: renamedPieces);

        var loadedPieces = await svc.LoadPiecesAsync();
        Assert.Single(loadedPieces);
        Assert.Equal("Concertino for Orchestra", loadedPieces[0].Form);
        var loadedPl = await svc.LoadPickListsAsync();
        Assert.Equal(new[] { "Concertino for Orchestra" }, loadedPl.Forms);
    }

    [Fact]
    public async Task SaveBatchAsync_DownstreamFailure_RollsBackPickListsToo()
    {
        // The "fire-and-forget" bug being retired here (Rework C14): a pieces
        // save chained after a pick-lists save could fail silently, leaving
        // the pick list with the new name and the pieces still referencing
        // the old one. With the atomic batch, any downstream subsystem
        // failure rolls the staged pick-list change back too.
        //
        // We force the failure on the albums layer (duplicate track numbers
        // on the same disc — guaranteed UNIQUE index violation in
        // SaveAlbumsCoreAsync) because SavePiecesCoreAsync silently skips
        // pieces whose composer doesn't exist instead of throwing. Either
        // failure shape exercises the same transaction-rollback path.
        var svc = NewService(out _);

        await svc.SaveBatchAsync(
            pickLists: new CanonPickLists
            {
                Forms = new List<string> { "Symphony" },
            });

        var newPl = new CanonPickLists
        {
            Forms = new List<string> { "Symphony for Orchestra" },   // the "renamed" value
        };
        var doomedAlbums = new List<CanonAlbum>
        {
            AlbumWithDuplicateTrackNumbers("Doomed album"),
        };

        await Assert.ThrowsAnyAsync<Exception>(() =>
            svc.SaveBatchAsync(pickLists: newPl, albums: doomedAlbums));

        // Pick list still contains the pre-rename value — proves the rollback
        // covered the pick-list staging, not just the albums save.
        var loadedPl = await svc.LoadPickListsAsync();
        Assert.Equal(new[] { "Symphony" }, loadedPl.Forms);
        Assert.Empty(await svc.LoadAlbumsAsync());
    }
}
