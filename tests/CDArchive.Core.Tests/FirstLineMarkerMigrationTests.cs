using CDArchive.Core.Data;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers the FirstLine-retirement migration: legacy <c>kind='FirstLine'</c>
/// markers in <c>piece_markers</c> are folded into the owning piece's title
/// (set-if-empty) and then deleted, before EF ever materialises them. The
/// migration runs as raw SQL because the <c>MarkerKind</c> enum no longer has
/// a <c>FirstLine</c> member — EF would otherwise fail to read the text-valued
/// <c>kind</c> column.
/// </summary>
public class FirstLineMarkerMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public FirstLineMarkerMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task FirstLineMarkers_FoldedIntoTitleWhenEmpty_AndDeleted()
    {
        // Prime the schema.
        await NewService().LoadPiecesAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await Exec(conn, "INSERT INTO composers (id, name, sort_name, is_provisional) VALUES (1, 'X', 'X', 0)");
            // Piece A: no title → should receive the first-line text.
            await Exec(conn, """
                INSERT INTO pieces (id, composer_id, position, is_provisional, title,
                                    catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix,
                                    form, number, nickname)
                VALUES (10, 1, 0, 1, NULL, '', 0, '', NULL, 1, NULL)
                """);
            // Piece B: has a title → keep it, drop the marker.
            await Exec(conn, """
                INSERT INTO pieces (id, composer_id, position, is_provisional, title,
                                    catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                VALUES (11, 1, 1, 1, 'Aria', '', 0, '')
                """);
            await Exec(conn,
                "INSERT INTO piece_markers (piece_id, position, kind, value) " +
                "VALUES (10, 0, 'FirstLine', 'Wenn mein Schatz Hochzeit macht')");
            await Exec(conn,
                "INSERT INTO piece_markers (piece_id, position, kind, value) " +
                "VALUES (11, 0, 'FirstLine', 'Erbarme dich')");
        }

        // New service → migration runs at init.
        await NewService().LoadPiecesAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();

            // Piece A (blank title) got the first line.
            Assert.Equal("Wenn mein Schatz Hochzeit macht",
                await ScalarString(conn, "SELECT title FROM pieces WHERE id=10"));
            // Piece B kept its real title.
            Assert.Equal("Aria",
                await ScalarString(conn, "SELECT title FROM pieces WHERE id=11"));
            // No FirstLine markers remain.
            Assert.Equal(0L,
                await ScalarLong(conn, "SELECT COUNT(*) FROM piece_markers WHERE kind='FirstLine'"));
        }
    }

    [Fact]
    public async Task Migration_Idempotent_NoFirstLineMarkers_NoOp()
    {
        // Fresh DB with no FirstLine markers — migration probe short-circuits.
        await NewService().LoadPiecesAsync();
        // Re-run init twice; must not throw.
        await NewService().LoadPiecesAsync();
        await NewService().LoadPiecesAsync();

        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        Assert.Equal(0L,
            await ScalarLong(conn, "SELECT COUNT(*) FROM piece_markers WHERE kind='FirstLine'"));
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarString(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return v == DBNull.Value ? null : (string?)v;
    }

    private static async Task<long> ScalarLong(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(v);
    }
}
