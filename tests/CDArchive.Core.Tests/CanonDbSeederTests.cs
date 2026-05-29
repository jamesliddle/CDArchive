using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Rework H42 + H43 regression tests for <see cref="CanonDbSeeder"/>:
///
/// <list type="bullet">
///   <item>H42 — every row builder must copy <c>IsProvisional</c> from the
///     source JSON model. Pre-fix the row classes' C# default
///     (<c>= true</c>) silently overwrote whatever the JSON said, so every
///     reseed wiped every approval the user had ever applied.</item>
///   <item>H43 — <see cref="CanonDbSeeder.SeedAsync"/> wraps its three
///     <c>SaveChangesAsync</c> calls in a single transaction. A failure
///     mid-seed rolls every subsystem back instead of leaving the DB in a
///     partial state with composers and pieces committed but no albums.</item>
/// </list>
/// </summary>
public class CanonDbSeederTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DbContextOptions<CanonDbContext> _options;

    public CanonDbSeederTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_seeder_{Guid.NewGuid():N}.db");
        _options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort */ }
    }

    private CanonDbContext NewDb()
    {
        var db = new CanonDbContext(_options);
        db.Database.EnsureCreated();
        return db;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H42 — JSON IsProvisional values survive seed
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The H42 regression itself: an approved composer / piece / album /
    /// track exported to JSON (with <c>is_provisional: false</c>) and
    /// reseeded must keep its approval. Pre-fix the row builders skipped
    /// the field, fell through to the row class's <c>= true</c> default,
    /// and every reseed wiped every approval.
    /// </summary>
    [Fact]
    public async Task SeedAsync_PreservesApprovedIsProvisionalFromJson()
    {
        var composers = new List<CanonComposer>
        {
            new()
            {
                Name = "Beethoven, Ludwig van",
                SortName = "Beethoven, Ludwig van",
                IsProvisional = false,
            },
        };
        var pieces = new List<CanonPiece>
        {
            new()
            {
                Composer = "Beethoven, Ludwig van",
                Title = "Symphony No. 9",
                IsProvisional = false,
                Subpieces = new List<CanonPiece>
                {
                    new() { Title = "1. Allegro", IsProvisional = false },
                },
            },
        };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Karajan / DG",
                IsProvisional = false,
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack>
                        {
                            new() { TrackNumber = 1, IsProvisional = false },
                        },
                    },
                },
            },
        };

        await using (var db = NewDb())
        {
            var seeder = new CanonDbSeeder(db);
            await seeder.SeedAsync(composers, pieces, new CanonPickLists(), albums);
        }

        await using (var db = NewDb())
        {
            var dbComposer = await db.Composers.SingleAsync();
            var dbPiece    = await db.Pieces.Where(p => p.ParentPieceId == null).SingleAsync();
            var dbSubpiece = await db.Pieces.Where(p => p.ParentPieceId != null).SingleAsync();
            var dbAlbum    = await db.Albums.SingleAsync();
            var dbTrack    = await db.AlbumTracks.SingleAsync();

            Assert.False(dbComposer.IsProvisional);
            Assert.False(dbPiece.IsProvisional);
            Assert.False(dbSubpiece.IsProvisional);  // H42 reaches into subpieces via MapPiece recursion
            Assert.False(dbAlbum.IsProvisional);
            Assert.False(dbTrack.IsProvisional);
        }
    }

    /// <summary>
    /// Back-compat: a JSON model without an explicit IsProvisional value
    /// (model default = true) should still seed as provisional. New rows
    /// start provisional and are opted into the canon by an explicit
    /// approve — this is the contract for fresh data.
    /// </summary>
    [Fact]
    public async Task SeedAsync_DefaultIsProvisionalIsTrueForFreshRows()
    {
        // Model fields default to IsProvisional = true.
        var composers = new List<CanonComposer>
        {
            new() { Name = "Test, Composer", SortName = "Test, Composer" },
        };
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Test, Composer", Title = "Test Piece" },
        };

        await using (var db = NewDb())
        {
            var seeder = new CanonDbSeeder(db);
            await seeder.SeedAsync(composers, pieces, new CanonPickLists(), new List<CanonAlbum>());
        }

        await using (var db = NewDb())
        {
            Assert.True((await db.Composers.SingleAsync()).IsProvisional);
            Assert.True((await db.Pieces.SingleAsync()).IsProvisional);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // H43 — atomic seed: mid-seed failure rolls everything back
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The H43 contract: when <see cref="CanonDbSeeder.SeedAlbums"/> hits a
    /// constraint violation, the composers and pieces saved earlier in the
    /// same call must also roll back. Force the failure with two album
    /// tracks numbered #1 on the same disc — the unique index on
    /// <c>album_tracks(disc_id, track_number)</c> rejects the second.
    /// </summary>
    [Fact]
    public async Task SeedAsync_AlbumFailureRollsBackComposersAndPieces()
    {
        var composers = new List<CanonComposer>
        {
            new() { Name = "Test, Composer", SortName = "Test, Composer" },
        };
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Test, Composer", Title = "Test Piece" },
        };
        // Doomed album: duplicate (disc_id, track_number) — unique-index
        // violation guaranteed during SeedAlbums' SaveChanges.
        var doomedAlbums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Doomed Album",
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack>
                        {
                            new() { TrackNumber = 1, Description = "first" },
                            new() { TrackNumber = 1, Description = "duplicate" },
                        },
                    },
                },
            },
        };

        await using (var db = NewDb())
        {
            var seeder = new CanonDbSeeder(db);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                seeder.SeedAsync(composers, pieces, new CanonPickLists(), doomedAlbums));
        }

        // Pre-fix the composer + piece rows would have survived the failed
        // album seed because SeedComposers + SeedPieces each ran their own
        // SaveChanges before SeedAlbums ran its failing one. Under the
        // transaction wrap, every row rolls back to an empty DB.
        await using (var db = NewDb())
        {
            Assert.Empty(await db.Composers.ToListAsync());
            Assert.Empty(await db.Pieces.ToListAsync());
            Assert.Empty(await db.Albums.ToListAsync());
        }
    }

    /// <summary>
    /// Happy path with the transaction wrap: a successful three-stage seed
    /// still commits. (Regression guard against accidentally leaving the
    /// transaction uncommitted in some refactor.)
    /// </summary>
    [Fact]
    public async Task SeedAsync_SuccessfulSeed_StillCommits()
    {
        var composers = new List<CanonComposer>
        {
            new() { Name = "Test, Composer", SortName = "Test, Composer" },
        };
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Test, Composer", Title = "Test Piece" },
        };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Test Album",
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack> { new() { TrackNumber = 1 } },
                    },
                },
            },
        };

        await using (var db = NewDb())
        {
            var seeder = new CanonDbSeeder(db);
            await seeder.SeedAsync(composers, pieces, new CanonPickLists(), albums);
        }

        await using (var db = NewDb())
        {
            Assert.Single(await db.Composers.ToListAsync());
            Assert.Single(await db.Pieces.ToListAsync());
            Assert.Single(await db.Albums.ToListAsync());
        }
    }
}
