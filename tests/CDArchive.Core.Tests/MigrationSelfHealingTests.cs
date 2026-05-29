using CDArchive.Core.Data;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests Rework C6's self-healing properties of <see cref="SqliteCanonDataService"/>'s
/// schema-migration path:
///
/// <list type="bullet">
///   <item><see cref="SqliteCanonDataService.WithForeignKeysOffAsync"/> restores
///     <c>PRAGMA foreign_keys=ON</c> via a <c>finally</c> block even when the
///     body throws — defends against the connection-pool poison-state where
///     a recycled connection silently lives on with FK enforcement off.</item>
///   <item><see cref="SqliteCanonDataService.DropOrphanRecreateTablesAsync"/>
///     finds and drops any leftover <c>*_new</c> sibling tables before the
///     live migrations run — defends against the unlikely-but-cheap-to-handle
///     case of a half-completed CREATE-COPY-DROP-RENAME recipe.</item>
/// </list>
/// </summary>
public class MigrationSelfHealingTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public MigrationSelfHealingTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.SelfHeal." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "ClassicalCanon.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private SqliteCanonDataService NewService()
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        return new SqliteCanonDataService(factory, json);
    }

    private static async Task<int> ReadForeignKeysPragmaAsync(SqliteConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys";
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Happy path: WithForeignKeysOffAsync runs the body with FKs off, then
    /// restores them. A read of the PRAGMA after the helper returns reports 1.
    /// </summary>
    [Fact]
    public async Task WithForeignKeysOff_RestoresPragmaAfterSuccessfulBody()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        // Microsoft.Data.Sqlite opens with FKs on by default; confirm baseline.
        Assert.Equal(1, await ReadForeignKeysPragmaAsync(conn));

        var observedInsideBody = -1;
        await SqliteCanonDataService.WithForeignKeysOffAsync(conn, async () =>
        {
            observedInsideBody = await ReadForeignKeysPragmaAsync(conn);
        });

        Assert.Equal(0, observedInsideBody);
        Assert.Equal(1, await ReadForeignKeysPragmaAsync(conn));
    }

    /// <summary>
    /// The C6 regression itself: when the body throws, FKs must still be
    /// restored — otherwise the connection is poisoned and any caller that
    /// reuses it silently runs without FK enforcement.
    /// </summary>
    [Fact]
    public async Task WithForeignKeysOff_RestoresPragmaWhenBodyThrows()
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        Assert.Equal(1, await ReadForeignKeysPragmaAsync(conn));

        var sentinel = new InvalidOperationException("simulated migration failure");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqliteCanonDataService.WithForeignKeysOffAsync(conn, () => throw sentinel));
        Assert.Same(sentinel, thrown);

        // The PRAGMA was restored on the way out of the finally block, even
        // though the body propagated. A subsequent caller on this same
        // connection would see FK enforcement back on.
        Assert.Equal(1, await ReadForeignKeysPragmaAsync(conn));
    }

    /// <summary>
    /// DropOrphanRecreateTablesAsync finds tables named `<x>_new` and drops
    /// them. Seeds an orphan, runs the cleanup directly, verifies it's gone.
    /// </summary>
    [Fact]
    public async Task DropOrphanRecreateTablesAsync_DropsOrphanNewTables()
    {
        // Build the schema once via the normal path.
        var svc = NewService();
        await svc.LoadAlbumsAsync();

        // Seed an orphan `album_tracks_new` table by hand — same shape as the
        // real migration's intermediate table would be, though shape is
        // irrelevant: only the name matters for the orphan detector.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE album_tracks_new (id INTEGER PRIMARY KEY)";
            await cmd.ExecuteNonQueryAsync();

            Assert.True(await TableExistsAsync(conn, "album_tracks_new"));
        }

        // Run the cleanup directly (the helper is internal).
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        await using (var db = new CanonDbContext(options))
        {
            await SqliteCanonDataService.DropOrphanRecreateTablesAsync(db);
        }

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            Assert.False(await TableExistsAsync(conn, "album_tracks_new"));
            // The live tables stay put — the LIKE pattern only matches the
            // `_new` suffix, not real schema tables.
            Assert.True(await TableExistsAsync(conn, "album_tracks"));
            Assert.True(await TableExistsAsync(conn, "albums"));
        }
    }

    /// <summary>
    /// Defensive: a healthy DB has no `*_new` tables, so the cleanup must
    /// not affect anything. Runs through the full EnsureInitialized path.
    /// </summary>
    [Fact]
    public async Task DropOrphanRecreateTablesAsync_IsNoOpOnHealthyDb()
    {
        var svc = NewService();
        await svc.LoadAlbumsAsync();

        // EnsureInitialized has already run the cleanup once (via
        // ApplySchemaUpgradesAsync). Run it explicitly a second time — it
        // should still find nothing and leave every table intact.
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        await using (var db = new CanonDbContext(options))
        {
            await SqliteCanonDataService.DropOrphanRecreateTablesAsync(db);
        }

        // Round-trip a save / load to confirm the schema still works.
        await svc.SaveAlbumsAsync(new List<Models.CanonAlbum>());
        var loaded = await svc.LoadAlbumsAsync();
        Assert.Empty(loaded);
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection conn, string name)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }
}
