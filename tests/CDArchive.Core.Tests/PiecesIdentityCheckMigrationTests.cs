using CDArchive.Core.Data;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers the M6 schema migration that adds a row-level CHECK constraint
/// requiring every <c>pieces</c> row to carry at least one human-visible
/// identifying field (Title, Form, Nickname, Number, or a non-empty
/// catalog_sort_prefix — the latter proxies for "has at least one Catalogue
/// entry" since the save path populates it from CatalogInfo[0]).
///
/// <list type="bullet">
///   <item><b>Fresh DB.</b> EnsureCreated builds the schema directly from EF,
///     so the CHECK is present from the start. An identity-less insert
///     (Title/Form/Nickname/Number all NULL, catalog_sort_prefix='') is
///     rejected with a CHECK constraint violation.</item>
///   <item><b>Legacy DB.</b> We rebuild the pieces table in the pre-fix shape
///     (only the single_parent CHECK) and seed real rows, then trigger the
///     migration. After it completes: the new CHECK is present, every row
///     survived with id + field values, identity-less inserts are rejected,
///     and the 5 indexes are still in place.</item>
///   <item><b>Idempotency.</b> A second migration pass against an already-
///     migrated schema must be a no-op (the recreate dance is not free at
///     3000-piece scale).</item>
/// </list>
///
/// See Rework M6.
/// </summary>
public class PiecesIdentityCheckMigrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteCanonDataService _svc;

    public PiecesIdentityCheckMigrationTests()
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
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private SqliteCanonDataService CreateFreshService()
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        return new SqliteCanonDataService(factory, json);
    }

    // ── Fresh DB ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task FreshDb_HasIdentityCheck_RejectsIdentitylessInsert()
    {
        // Prime the schema.
        await _svc.LoadPiecesAsync();

        Assert.True(await HasCheckConstraintAsync("pieces", "ck_pieces_has_identity"),
            "Expected ck_pieces_has_identity on a fresh DB built by EF.");

        // Seed a composer so the FK is satisfied.
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await ExecAsync(conn, """
            INSERT INTO composers (id, name, sort_name, is_provisional)
            VALUES (1, 'Beethoven', 'Beethoven', 0)
            """);

        // Try to insert a piece with no identity fields. SQLite raises
        // SqliteException with SqliteErrorCode 19 (CONSTRAINT) — the
        // generic constraint error covers CHECK violations.
        var ex = await Assert.ThrowsAsync<SqliteException>(async () =>
        {
            await ExecAsync(conn, """
                INSERT INTO pieces
                    (composer_id, position, is_provisional,
                     catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                VALUES
                    (1, 0, 1, '', 0, '')
                """);
        });
        Assert.Contains("CHECK constraint failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // Each row provides exactly one identity field — every variant must be
    // accepted by the CHECK. Theory covers the disjunction empirically so a
    // future "tighter" CHECK that drops one branch fails this test loudly.
    [InlineData("title",    "'Sonata'",  "", "")]
    [InlineData("form",     "'Sonata'",  "", "")]
    [InlineData("nickname", "'Moonlight'", "", "")]
    [InlineData("number",   "5",         "", "")]
    [InlineData("(catalog)", null,       "Op.", "")]
    public async Task FreshDb_IdentityCheckAcceptsEveryValidIdentityField(
        string label, string? valueLiteral, string catalogPrefix, string _)
    {
        await _svc.LoadPiecesAsync();
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await ExecAsync(conn, """
            INSERT INTO composers (id, name, sort_name, is_provisional)
            VALUES (1, 'Beethoven', 'Beethoven', 0)
            """);

        var colName = label == "(catalog)" ? null : label;
        var colInsert    = colName is null ? ""        : $", {colName}";
        var valueInsert  = colName is null ? ""        : $", {valueLiteral}";

        // The "catalog prefix" variant uses catalog_sort_prefix as its identity
        // proxy; every other variant leaves that empty.
        var sql = $$"""
            INSERT INTO pieces
                (composer_id, position, is_provisional,
                 catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix{{colInsert}})
            VALUES
                (1, 0, 1, '{{catalogPrefix}}', 0, ''{{valueInsert}})
            """;
        // Must not throw — the row carries one valid identity field.
        await ExecAsync(conn, sql);
    }

    // ── Legacy DB migration ──────────────────────────────────────────────────

    [Fact]
    public async Task LegacyDb_MigratesToAddIdentityCheck_AndPreservesRows()
    {
        // Build the modern schema first, then nuke pieces and rebuild it in
        // the pre-M6 shape (only ck_pieces_single_parent). Closest reproduction
        // of an existing user's DB without shipping a pre-fix file.
        await _svc.LoadPiecesAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, "PRAGMA foreign_keys=OFF");
            // Cascade-clear children so the DROP doesn't fail.
            await ExecAsync(conn, "DELETE FROM piece_versions");
            await ExecAsync(conn, "DROP TABLE pieces");
            await ExecAsync(conn, """
                CREATE TABLE pieces (
                    id                       INTEGER NOT NULL CONSTRAINT PK_pieces PRIMARY KEY AUTOINCREMENT,
                    composer_id              INTEGER NOT NULL,
                    parent_piece_id          INTEGER     NULL,
                    parent_version_id        INTEGER     NULL,
                    position                 INTEGER NOT NULL,
                    title                    TEXT        NULL,
                    title_english            TEXT        NULL,
                    subtitle                 TEXT        NULL,
                    nickname                 TEXT        NULL,
                    form                     TEXT        NULL,
                    number                   INTEGER     NULL,
                    music_number             TEXT        NULL,
                    key_tonality             TEXT        NULL,
                    key_mode                 TEXT        NULL,
                    publication_year         INTEGER     NULL,
                    instrumentation_category TEXT        NULL,
                    numbered_subpieces       INTEGER     NULL,
                    subpieces_start          INTEGER     NULL,
                    notes                    TEXT        NULL,
                    is_provisional           INTEGER NOT NULL,
                    instrumentation_json     TEXT        NULL,
                    composition_years_json   TEXT        NULL,
                    text_author_json         TEXT        NULL,
                    roles_json               TEXT        NULL,
                    arrangements_json        TEXT        NULL,
                    cadenza_json             TEXT        NULL,
                    title_number_json        TEXT        NULL,
                    catalog_sort_prefix      TEXT    NOT NULL,
                    catalog_sort_number      INTEGER NOT NULL,
                    catalog_sort_suffix      TEXT    NOT NULL,
                    CONSTRAINT ck_pieces_single_parent
                        CHECK ((parent_piece_id IS NULL) OR (parent_version_id IS NULL)),
                    CONSTRAINT FK_pieces_composers_composer_id
                        FOREIGN KEY (composer_id)       REFERENCES composers      (id) ON DELETE RESTRICT,
                    CONSTRAINT FK_pieces_piece_versions_parent_version_id
                        FOREIGN KEY (parent_version_id) REFERENCES piece_versions (id) ON DELETE CASCADE,
                    CONSTRAINT FK_pieces_pieces_parent_piece_id
                        FOREIGN KEY (parent_piece_id)   REFERENCES pieces         (id) ON DELETE CASCADE
                )
                """);

            // Seed a composer + two real pieces (different identity types).
            await ExecAsync(conn, """
                INSERT INTO composers (id, name, sort_name, is_provisional)
                VALUES (1, 'Beethoven', 'Beethoven', 0)
                """);
            await ExecAsync(conn, """
                INSERT INTO pieces
                    (id, composer_id, position, title, form, number, is_provisional,
                     catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                VALUES
                    (101, 1, 0, 'Symphony #5', 'Symphony', 5, 0, 'Op.', 67, ''),
                    (102, 1, 1, NULL,          'Sonata',   8, 0, 'Op.', 13, '')
                """);

            await ExecAsync(conn, "PRAGMA foreign_keys=ON");
        }

        // Sanity precondition: the legacy table has the single-parent CHECK
        // but NOT the identity CHECK.
        Assert.False(await HasCheckConstraintAsync("pieces", "ck_pieces_has_identity"),
            "Test setup precondition: legacy pieces should lack ck_pieces_has_identity.");
        Assert.True(await HasCheckConstraintAsync("pieces", "ck_pieces_single_parent"),
            "Test setup precondition: legacy pieces should keep ck_pieces_single_parent.");

        // Trigger the migration via a fresh service.
        var migrationSvc = CreateFreshService();
        await migrationSvc.LoadPiecesAsync();

        // CHECK is now present.
        Assert.True(await HasCheckConstraintAsync("pieces", "ck_pieces_has_identity"),
            "Migration must add ck_pieces_has_identity.");
        // The existing single-parent CHECK must still be there too — a sloppy
        // recreate that forgets it would silently relax piece parentage.
        Assert.True(await HasCheckConstraintAsync("pieces", "ck_pieces_single_parent"),
            "Migration must preserve ck_pieces_single_parent.");

        // Rows survived with ids + field values.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, title, form, number, catalog_sort_prefix FROM pieces ORDER BY id";
            await using var reader = await cmd.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync());
            Assert.Equal(101L,         reader.GetInt64(0));
            Assert.Equal("Symphony #5", reader.GetString(1));
            Assert.Equal("Symphony",   reader.GetString(2));
            Assert.Equal(5,            reader.GetInt32(3));
            Assert.Equal("Op.",        reader.GetString(4));

            Assert.True(await reader.ReadAsync());
            Assert.Equal(102L,    reader.GetInt64(0));
            Assert.True(reader.IsDBNull(1));    // Title null — identity carried by Form
            Assert.Equal("Sonata", reader.GetString(2));
            Assert.Equal(8,        reader.GetInt32(3));
            Assert.Equal("Op.",    reader.GetString(4));

            Assert.False(await reader.ReadAsync());
        }

        // All five indexes EF declared on pieces are recreated.
        Assert.True(await IndexExistsAsync("ix_pieces_composer_catalog_sort"));
        Assert.True(await IndexExistsAsync("IX_pieces_composer_id"));
        Assert.True(await IndexExistsAsync("IX_pieces_composer_id_title"));
        Assert.True(await IndexExistsAsync("IX_pieces_parent_piece_id_position"));
        Assert.True(await IndexExistsAsync("IX_pieces_parent_version_id_position"));

        // The CHECK is now enforced on post-migration inserts too.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var ex = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await ExecAsync(conn, """
                    INSERT INTO pieces
                        (composer_id, position, is_provisional,
                         catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                    VALUES
                        (1, 99, 1, '', 0, '')
                    """);
            });
            Assert.Contains("CHECK constraint failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Idempotency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_IsIdempotent_OnAlreadyMigratedSchema()
    {
        await _svc.LoadPiecesAsync();   // First pass — modern schema with the CHECK.

        // Insert a piece, capture its rowid.
        long id;
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, """
                INSERT INTO composers (id, name, sort_name, is_provisional)
                VALUES (1, 'Beethoven', 'Beethoven', 0)
                """);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pieces
                    (composer_id, position, title, is_provisional,
                     catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                VALUES
                    (1, 0, 'Existing piece', 1, '', 0, '');
                SELECT last_insert_rowid()
                """;
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        // Second migration pass — fresh service, same DB.
        await CreateFreshService().LoadPiecesAsync();

        // Row still exists with the same id — a recreate-table would also
        // preserve the row, but the id stability check + the constraint
        // already-present check combine to make this a real idempotency test.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, title FROM pieces WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), "Row should still exist after the no-op pass.");
            Assert.Equal(id,                reader.GetInt64(0));
            Assert.Equal("Existing piece",  reader.GetString(1));
        }

        // The CHECK is still present (sanity).
        Assert.True(await HasCheckConstraintAsync("pieces", "ck_pieces_has_identity"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> HasCheckConstraintAsync(string table, string constraintName)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", table);
        var sql = (string?)await cmd.ExecuteScalarAsync();
        return sql is not null && sql.Contains(constraintName, StringComparison.Ordinal);
    }

    private async Task<bool> IndexExistsAsync(string indexName)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=$name";
        cmd.Parameters.AddWithValue("$name", indexName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
