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

    private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
    {
        private readonly DbContextOptions<CanonDbContext> _options;
        public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
        public CanonDbContext CreateDbContext() => new(_options);
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
}
