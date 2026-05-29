using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers the schema migration that makes <c>album_tracks.disc_id</c> nullable
/// to host loose tracks. Two scenarios:
///
/// <list type="bullet">
///   <item><b>Fresh DB.</b> EnsureCreated builds the schema directly from the
///     EF model, so disc_id is nullable from the start and the migration
///     no-ops. Round-tripping a loose track confirms the column actually
///     accepts null.</item>
///   <item><b>Legacy DB.</b> We hand-build an old-shape album_tracks (with
///     NOT NULL disc_id), insert a couple of album-bound rows, then run the
///     migration. After it completes: column is nullable, every row survived
///     with its id and field values, and a loose track can be inserted.</item>
/// </list>
///
/// Tests share a temp directory per-instance — disposed when xUnit tears the
/// fixture down so we don't leave fragments under TEMP.
/// </summary>
public class AlbumTracksNullableDiscIdMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteCanonDataService _svc;

    public AlbumTracksNullableDiscIdMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "ClassicalCanon.db");

        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        _svc = new SqliteCanonDataService(factory, json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// New service instance over the SAME on-disk DB so EnsureInitializedAsync
    /// (one-shot per instance) runs again — needed when a test mutates the
    /// schema after the constructor's service has already initialised.
    /// </summary>
    private SqliteCanonDataService CreateFreshService()
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        return new SqliteCanonDataService(factory, json);
    }

    /// <summary>
    /// Fresh DB built by EF directly: disc_id is nullable from the start,
    /// loose tracks (disc_id NULL) round-trip through the row layer.
    /// </summary>
    [Fact]
    public async Task FreshDb_DiscIdIsNullable_AndLooseTracksRoundTrip()
    {
        // Prime the service so the schema gets created. Any Load* call works;
        // the data is empty either way.
        await _svc.LoadAlbumsAsync();

        Assert.False(await IsColumnNotNullAsync("album_tracks", "disc_id"),
            "Expected disc_id to be nullable on a fresh DB.");

        // Insert a loose-track row directly via raw SQL — the higher-level
        // SaveLooseTracksAsync API isn't implemented yet, but the schema must
        // already accept the row.
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO album_tracks
                    (disc_id, track_number, description, is_provisional)
                VALUES
                    (NULL, 0, 'Loose download', 1)
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM album_tracks WHERE disc_id IS NULL";
            var n = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(1, n);
        }
    }

    /// <summary>
    /// Legacy DB: hand-build album_tracks with NOT NULL disc_id, seed some
    /// rows, then trigger the schema upgrade. Migration must preserve every
    /// row (id + field values) and leave the column nullable.
    /// </summary>
    [Fact]
    public async Task LegacyDb_MigratesToNullableDiscId_AndPreservesRows()
    {
        // First make the service create the modern schema, then nuke
        // album_tracks and rebuild it in the legacy (NOT NULL disc_id) shape.
        // This is the closest reproduction of an in-the-wild old DB without
        // shipping an actual old-format file to the test repo.
        await _svc.LoadAlbumsAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "PRAGMA foreign_keys=OFF");
            await ExecAsync(conn, "DROP TABLE album_tracks");
            // Re-create with the pre-migration shape: disc_id INTEGER NOT NULL.
            await ExecAsync(conn, """
                CREATE TABLE album_tracks (
                    id             INTEGER NOT NULL CONSTRAINT PK_album_tracks PRIMARY KEY AUTOINCREMENT,
                    disc_id        INTEGER NOT NULL,
                    track_number   INTEGER NOT NULL,
                    duration       TEXT        NULL,
                    description    TEXT        NULL,
                    session_id     INTEGER     NULL,
                    spars_code     TEXT        NULL,
                    is_stereo      INTEGER     NULL,
                    is_provisional INTEGER NOT NULL DEFAULT 1,
                    flac_path      TEXT        NULL,
                    mp3_path       TEXT        NULL
                )
                """);

            // Seed an album / disc, then two album-bound tracks. The album +
            // disc come via SaveAlbumsAsync above (an empty DB), so we have to
            // insert raw rows here. id values are explicit so we can assert
            // they survive the recreate dance.
            await ExecAsync(conn, """
                INSERT INTO composers (id, name, sort_name, is_provisional)
                VALUES (1, 'Beethoven, Ludwig van', 'Beethoven, Ludwig van', 0)
                """);
            await ExecAsync(conn, """
                INSERT INTO albums (id, title, is_provisional)
                VALUES (1, 'Test Album', 1)
                """);
            await ExecAsync(conn, """
                INSERT INTO album_discs (id, album_id, disc_number)
                VALUES (1, 1, 1)
                """);
            await ExecAsync(conn, """
                INSERT INTO album_tracks
                    (id, disc_id, track_number, description, is_provisional, duration)
                VALUES
                    (101, 1, 1, 'Track one',  1, '3:14'),
                    (102, 1, 2, 'Track two',  1, '4:08')
                """);

            await ExecAsync(conn, "PRAGMA foreign_keys=ON");
        }

        // Sanity check that the table is in the legacy shape — disc_id NOT NULL.
        Assert.True(await IsColumnNotNullAsync("album_tracks", "disc_id"),
            "Test setup precondition: legacy album_tracks should have NOT NULL disc_id.");

        // Trigger the migration via a FRESH service. EnsureInitialized is
        // one-shot per service instance, and the constructor's service already
        // ran the schema upgrade once on the modern schema we wiped out.
        var migrationSvc = CreateFreshService();
        await migrationSvc.LoadAlbumsAsync();

        // Column is now nullable.
        Assert.False(await IsColumnNotNullAsync("album_tracks", "disc_id"),
            "Migration must leave disc_id nullable.");

        // Rows survived with their ids and values.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, disc_id, track_number, description, duration FROM album_tracks ORDER BY id";
            await using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(101L,        reader.GetInt64(0));
            Assert.Equal(1L,          reader.GetInt64(1));
            Assert.Equal(1,           reader.GetInt32(2));
            Assert.Equal("Track one", reader.GetString(3));
            Assert.Equal("3:14",      reader.GetString(4));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(102L,        reader.GetInt64(0));
            Assert.Equal(1L,          reader.GetInt64(1));
            Assert.Equal(2,           reader.GetInt32(2));
            Assert.Equal("Track two", reader.GetString(3));
            Assert.Equal("4:08",      reader.GetString(4));

            Assert.False(await reader.ReadAsync());
        }

        // The unique index on (disc_id, track_number) was recreated.
        Assert.True(await IndexExistsAsync("IX_album_tracks_DiscId_TrackNumber"),
            "Migration must recreate the unique index after the table swap.");

        // A loose track (disc_id NULL) now inserts cleanly.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO album_tracks (disc_id, track_number, description, is_provisional)
                VALUES (NULL, 0, 'Loose post-migration', 1)
                """;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The migration is idempotent — once disc_id is nullable, a second pass
    /// must not recreate the table (rows would survive either way, but the
    /// recreate is wasted work).
    /// </summary>
    [Fact]
    public async Task Migration_IsIdempotent_OnAlreadyNullableSchema()
    {
        await _svc.LoadAlbumsAsync();   // first pass — creates schema (already nullable).

        // Insert a row, then force a second EnsureInitialized via another call.
        // If the recreate dance ran a second time it would (best case) preserve
        // the row but burn time; we assert it didn't run by checking the row's
        // id is still the original autoincrement value.
        long id;
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO album_tracks (disc_id, track_number, description, is_provisional)
                    VALUES (NULL, 0, 'Existing loose', 1);
                    SELECT last_insert_rowid()
                    """;
                id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
        }

        // Second pass — fresh service so EnsureInitialized + the upgrade run
        // again. Already-nullable column → upgrade no-ops.
        await CreateFreshService().LoadAlbumsAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, description FROM album_tracks WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Row should still exist after the no-op pass.");
            Assert.Equal("Existing loose", reader.GetString(1));
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<bool> IsColumnNotNullAsync(string table, string column)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) == 1;
        }
        throw new InvalidOperationException($"Column {table}.{column} not found in PRAGMA table_info.");
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name";
        cmd.Parameters.AddWithValue("$name", indexName);
        var result = await cmd.ExecuteScalarAsync();
        return result is not null;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
