using CDArchive.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Services;

/// <summary>
/// Schema migrations subsystem (H1 slice 6 — the final slice of the H1
/// god-class split arc). Extracted from <c>SqliteCanonDataService.cs</c> via
/// partial-class split.
///
/// <para>What lives here:</para>
/// <list type="bullet">
///   <item><c>ApplySchemaUpgradesAsync</c> — the migration orchestrator,
///         called from <c>EnsureInitializedAsync</c> in the main file.</item>
///   <item><c>EnsureColumnAsync</c> — idempotent ALTER TABLE ADD COLUMN.</item>
///   <item><c>EnsureColumnNullableAsync</c> — idempotent column-nullability
///         flip via a caller-supplied recreate recipe (SQLite can't ALTER
///         COLUMN in place).</item>
///   <item><c>DropOrphanRecreateTablesAsync</c> — defensive cleanup of any
///         <c>*_new</c> tables left behind by a failed CREATE-COPY-DROP-RENAME
///         dance (C6).</item>
///   <item><c>RecreateAlbumTracksWithNullableDiscIdAsync</c> +
///         <c>RecreateAlbumPerformersWithNullableAlbumIdAsync</c> — the two
///         current concrete recreate recipes (more can be added next to them).</item>
///   <item><c>WithForeignKeysOffAsync</c> — the FK-OFF/ON wrapper with the
///         try/finally restore that C6 was about.</item>
///   <item><c>ExecAsync</c> — small raw-SQL helper shared between the
///         migrations.</item>
/// </list>
///
/// <para>Behaviour unchanged. Methods retain their visibility, signatures, and
/// dependency surface — they're all <c>static</c> here (only <c>ExecAsync</c>
/// is used by callers outside this partial, but it's currently only consumed
/// by the migrations themselves, so it stays private to this file). The
/// <c>EnsureInitializedAsync</c> orchestrator in the main file calls
/// <c>ApplySchemaUpgradesAsync</c> via shared partial-class state.</para>
///
/// <para>Two helpers are <c>internal static</c> so test fixtures can drive
/// them directly: <c>DropOrphanRecreateTablesAsync</c> and
/// <c>WithForeignKeysOffAsync</c>. Those visibility levels are preserved.</para>
/// </summary>
public partial class SqliteCanonDataService
{
    /// <summary>
    /// Applies idempotent ALTER TABLE migrations for columns added after the
    /// initial <c>EnsureCreatedAsync</c> snapshot. EF Core's <c>EnsureCreated</c>
    /// only creates the schema on a fresh database; it never alters an existing
    /// one. Each upgrade here checks <c>PRAGMA table_info</c> first so it's safe
    /// to run on every startup.
    ///
    /// <para>
    /// The migration default of <c>1</c> (true / provisional) matches the entity
    /// initializer: every composer and piece is provisional until explicitly
    /// approved. Rows already in the table when the column is first added are
    /// flipped to provisional too — the user opts each entry into the canon by
    /// approving it, rather than the curator opting bad data out.
    /// </para>
    /// </summary>
    private static async Task ApplySchemaUpgradesAsync(CanonDbContext db)
    {
        // Defensive cleanup for SQLite's recommended CREATE-COPY-DROP-RENAME
        // recipe. The recipe runs entirely inside a transaction, so a crash
        // mid-transaction rolls everything back — but defending against the
        // off-nominal case (process killed between COMMIT and the next op,
        // a third-party tool running half a migration manually, an aborted
        // run that left a `*_new` table tracked by sqlite_master) is cheap
        // and idempotent: scan once and drop any orphan tables named with
        // the `_new` suffix before the live migrations run. See Rework C6.
        await DropOrphanRecreateTablesAsync(db).ConfigureAwait(false);

        await EnsureColumnAsync(db, "composers", "is_provisional", "INTEGER NOT NULL DEFAULT 1")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "pieces", "is_provisional", "INTEGER NOT NULL DEFAULT 1")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "albums", "is_provisional", "INTEGER NOT NULL DEFAULT 1")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "album_tracks", "is_provisional", "INTEGER NOT NULL DEFAULT 1")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "album_tracks", "is_stereo", "INTEGER NULL")
            .ConfigureAwait(false);
        // Audio-file location columns for the music player. Optional — populated
        // by user input / the archive scan. Convention-based resolution doesn't
        // need them; they're escape hatches for outliers.
        await EnsureColumnAsync(db, "albums", "archive_folder", "TEXT NULL")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "album_discs", "folder_name", "TEXT NULL")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "album_tracks", "flac_path", "TEXT NULL")
            .ConfigureAwait(false);
        await EnsureColumnAsync(db, "album_tracks", "mp3_path", "TEXT NULL")
            .ConfigureAwait(false);

        // Recording-session State (province / state, sits between City and
        // Country in the address-style location summary). Nullable — most
        // existing rows have City+Country only and stay that way.
        await EnsureColumnAsync(db, "album_sessions", "state", "TEXT NULL")
            .ConfigureAwait(false);

        // Loose tracks (singletons that don't belong to any album) live in the
        // same album_tracks table but with disc_id NULL. The original schema
        // had disc_id NOT NULL — recreate the table on first upgrade so the
        // column accepts null. Safe to run on every startup; the helper checks
        // the current nullability and no-ops once it's already nullable.
        await EnsureColumnNullableAsync(db, "album_tracks", "disc_id",
            recreate: RecreateAlbumTracksWithNullableDiscIdAsync)
            .ConfigureAwait(false);

        // Performers on a loose track have no owning album, so album_id needs
        // to be nullable. The migration also adds a CHECK constraint guaranteeing
        // every performer row anchors on at least one of album_id / track_id —
        // catches buggy callers that would otherwise create floating credits.
        await EnsureColumnNullableAsync(db, "album_performers", "album_id",
            recreate: RecreateAlbumPerformersWithNullableAlbumIdAsync)
            .ConfigureAwait(false);

        // M6: ensure every piece carries at least one human-visible identifying
        // field (Title, Form, Nickname, Number, or a Catalogue entry — proxied
        // by non-empty catalog_sort_prefix). EF Core's EnsureCreatedAsync adds
        // the CHECK to fresh DBs; for existing DBs the recreate dance below
        // brings them up to date too. No-ops once the CHECK is present.
        await EnsureCheckConstraintAsync(db, "pieces", "ck_pieces_has_identity",
            recreate: RecreatePiecesWithIdentityCheckAsync)
            .ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        CanonDbContext db, string table, string column, string columnDef)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync().ConfigureAwait(false);

        bool exists = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef}";
        await alter.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotently makes <paramref name="column"/> nullable on <paramref name="table"/>.
    /// SQLite cannot alter a column's nullability in place, so the migration runs
    /// <paramref name="recreate"/> — which is expected to perform the standard
    /// CREATE-COPY-DROP-RENAME dance inside a transaction with foreign keys off.
    /// No-ops when <c>PRAGMA table_info</c> reports the column is already nullable.
    /// </summary>
    private static async Task EnsureColumnNullableAsync(
        CanonDbContext db, string table, string column,
        Func<System.Data.Common.DbConnection, Task> recreate)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync().ConfigureAwait(false);

        bool alreadyNullable = false;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
                if (!string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    continue;
                // notnull = 0 means the column is nullable.
                alreadyNullable = reader.GetInt32(3) == 0;
                break;
            }
        }

        if (alreadyNullable) return;
        await recreate(conn).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotently ensures <paramref name="constraintName"/> is present on
    /// <paramref name="table"/>. SQLite stores the table's CREATE statement
    /// verbatim in <c>sqlite_master.sql</c>; this helper scans that text for
    /// the constraint name and runs <paramref name="recreate"/> (the standard
    /// CREATE-COPY-DROP-RENAME recipe) when the name isn't present. No-ops on
    /// a healthy DB.
    /// <para>
    /// The match is on the constraint name as a substring of the CREATE
    /// statement. SQLite stores CHECK constraints inline within the CREATE
    /// statement, so this is a reliable way to detect their presence without
    /// parsing.
    /// </para>
    /// </summary>
    private static async Task EnsureCheckConstraintAsync(
        CanonDbContext db, string table, string constraintName,
        Func<System.Data.Common.DbConnection, Task> recreate)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync().ConfigureAwait(false);

        string? createSql = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name";
            var p = cmd.CreateParameter();
            p.ParameterName = "@name";
            p.Value = table;
            cmd.Parameters.Add(p);
            createSql = (string?)await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        }

        if (createSql is not null && createSql.Contains(constraintName, StringComparison.Ordinal))
            return;

        await recreate(conn).ConfigureAwait(false);
    }

    /// <summary>
    /// Drops any orphan tables left over from a failed or interrupted
    /// CREATE-COPY-DROP-RENAME recreate dance. SQLite's recommended schema-
    /// change recipe builds a sibling `<table>_new` and renames it into
    /// place at the end of a transaction; a crash before the COMMIT rolls
    /// the whole thing back, but a partial manual run, a third-party tool's
    /// half-migration, or a corrupted journal could leave the sibling
    /// behind. This is cheap to run on every startup and idempotent: on a
    /// healthy DB sqlite_master has no `*_new` entries and the SELECT
    /// returns nothing. See Rework C6.
    /// </summary>
    internal static async Task DropOrphanRecreateTablesAsync(CanonDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync().ConfigureAwait(false);

        var orphans = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%\\_new' ESCAPE '\\'";
            await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
                orphans.Add(reader.GetString(0));
        }

        foreach (var name in orphans)
        {
            // Use IF EXISTS as a belt-and-braces guard — sqlite_master and
            // the table list can theoretically drift in adversarial cases.
            await ExecAsync(conn, $"DROP TABLE IF EXISTS \"{name}\"").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Recreates <c>album_tracks</c> with a nullable <c>disc_id</c> column,
    /// preserving every existing row and its id. Implements SQLite's
    /// recommended schema-change recipe:
    /// <list type="number">
    ///   <item><c>PRAGMA foreign_keys=OFF</c> so dropping the old table doesn't
    ///     cascade through child tables (refs / performers).</item>
    ///   <item>Transaction. CREATE TABLE <c>album_tracks_new</c> with the new
    ///     definition (only <c>disc_id</c> changes nullability).</item>
    ///   <item>Copy every row, columns enumerated explicitly so the order is
    ///     pinned regardless of how columns happen to live in the old table.</item>
    ///   <item>DROP the old table; RENAME the new one into its place.</item>
    ///   <item>Recreate the <c>(disc_id, track_number)</c> unique index that
    ///     EF Core declared on the entity.</item>
    ///   <item><c>PRAGMA foreign_key_check</c> as a sanity gate before commit;
    ///     any orphaned child row would surface here.</item>
    ///   <item>COMMIT, then turn FKs back on.</item>
    /// </list>
    /// </summary>
    private static Task RecreateAlbumTracksWithNullableDiscIdAsync(
        System.Data.Common.DbConnection conn) =>
        WithForeignKeysOffAsync(conn, async () =>
        {
        await using (var tx = await conn.BeginTransactionAsync().ConfigureAwait(false))
        {
            // New table: disc_id nullable, everything else identical.
            await ExecAsync(conn, """
                CREATE TABLE album_tracks_new (
                    id             INTEGER NOT NULL CONSTRAINT PK_album_tracks PRIMARY KEY AUTOINCREMENT,
                    disc_id        INTEGER     NULL,
                    track_number   INTEGER NOT NULL,
                    duration       TEXT        NULL,
                    description    TEXT        NULL,
                    session_id     INTEGER     NULL,
                    spars_code     TEXT        NULL,
                    is_stereo      INTEGER     NULL,
                    is_provisional INTEGER NOT NULL DEFAULT 1,
                    flac_path      TEXT        NULL,
                    mp3_path       TEXT        NULL,
                    CONSTRAINT FK_album_tracks_album_discs_disc_id
                        FOREIGN KEY (disc_id)    REFERENCES album_discs    (id) ON DELETE CASCADE,
                    CONSTRAINT FK_album_tracks_album_sessions_session_id
                        FOREIGN KEY (session_id) REFERENCES album_sessions (id) ON DELETE SET NULL
                )
                """, tx);

            // Copy rows. Explicit column list so a stale column ordering in the
            // old table doesn't silently misalign.
            await ExecAsync(conn, """
                INSERT INTO album_tracks_new
                    (id, disc_id, track_number, duration, description, session_id,
                     spars_code, is_stereo, is_provisional, flac_path, mp3_path)
                SELECT
                     id, disc_id, track_number, duration, description, session_id,
                     spars_code, is_stereo, is_provisional, flac_path, mp3_path
                FROM album_tracks
                """, tx);

            await ExecAsync(conn, "DROP TABLE album_tracks", tx);
            await ExecAsync(conn, "ALTER TABLE album_tracks_new RENAME TO album_tracks", tx);

            // EF named its unique index IX_album_tracks_DiscId_TrackNumber. Keep
            // the name so future migrations can reference it without surprise.
            await ExecAsync(conn,
                "CREATE UNIQUE INDEX IX_album_tracks_DiscId_TrackNumber " +
                "ON album_tracks (disc_id, track_number)", tx);

            // Last-chance sanity gate inside the txn — any child row whose FK no
            // longer points at a valid parent would surface here. With FKs off
            // during the swap, the integrity check has to be explicit.
            await using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await check.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Foreign-key check failed after recreating album_tracks. " +
                        "Migration aborted; the transaction will roll back.");
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        });

    private static async Task ExecAsync(
        System.Data.Common.DbConnection conn, string sql,
        System.Data.Common.DbTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (tx != null) cmd.Transaction = tx;
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Recreates <c>pieces</c> with the <c>ck_pieces_has_identity</c> CHECK
    /// constraint added (alongside the existing <c>ck_pieces_single_parent</c>).
    /// The CHECK enforces "every piece must carry at least one human-visible
    /// identifying field" — Title, Form, Nickname, Number, or a non-empty
    /// catalog_sort_prefix (proxy for at least one Catalogue entry). Same
    /// recipe as the album_tracks / album_performers migrations:
    /// <list type="number">
    ///   <item>PRAGMA foreign_keys=OFF.</item>
    ///   <item>Transaction. CREATE TABLE <c>pieces_new</c> with the desired
    ///     schema — both CHECK constraints, FK to composers (Restrict), FK
    ///     self-reference for parent_piece_id (Cascade), FK to piece_versions
    ///     for parent_version_id (Cascade).</item>
    ///   <item>INSERT SELECT with explicit column list so the migration is
    ///     stable against future column-order changes.</item>
    ///   <item>DROP pieces; RENAME pieces_new → pieces. The self-FK in
    ///     pieces_new stored the literal "pieces" — once the table is renamed
    ///     in place, the reference becomes self-referential as intended.</item>
    ///   <item>Recreate the 5 indexes EF declared (composer, parent-piece+pos,
    ///     parent-version+pos, composer+title, composer+catalog-sort).</item>
    ///   <item>PRAGMA foreign_key_check sanity gate inside the txn; any
    ///     orphaned child row surfaces here.</item>
    ///   <item>COMMIT, then turn FKs back on.</item>
    /// </list>
    /// See Rework M6.
    /// </summary>
    private static Task RecreatePiecesWithIdentityCheckAsync(
        System.Data.Common.DbConnection conn) =>
        WithForeignKeysOffAsync(conn, async () =>
        {
        await using (var tx = await conn.BeginTransactionAsync().ConfigureAwait(false))
        {
            await ExecAsync(conn, """
                CREATE TABLE pieces_new (
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
                    CONSTRAINT ck_pieces_has_identity
                        CHECK (title IS NOT NULL OR form IS NOT NULL OR nickname IS NOT NULL OR
                               number IS NOT NULL OR catalog_sort_prefix != ''),
                    CONSTRAINT FK_pieces_composers_composer_id
                        FOREIGN KEY (composer_id)       REFERENCES composers      (id) ON DELETE RESTRICT,
                    CONSTRAINT FK_pieces_piece_versions_parent_version_id
                        FOREIGN KEY (parent_version_id) REFERENCES piece_versions (id) ON DELETE CASCADE,
                    CONSTRAINT FK_pieces_pieces_parent_piece_id
                        FOREIGN KEY (parent_piece_id)   REFERENCES pieces         (id) ON DELETE CASCADE
                )
                """, tx);

            await ExecAsync(conn, """
                INSERT INTO pieces_new
                    (id, composer_id, parent_piece_id, parent_version_id, position,
                     title, title_english, subtitle, nickname, form,
                     number, music_number, key_tonality, key_mode, publication_year,
                     instrumentation_category, numbered_subpieces, subpieces_start, notes, is_provisional,
                     instrumentation_json, composition_years_json, text_author_json, roles_json,
                     arrangements_json, cadenza_json, title_number_json,
                     catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)
                SELECT
                     id, composer_id, parent_piece_id, parent_version_id, position,
                     title, title_english, subtitle, nickname, form,
                     number, music_number, key_tonality, key_mode, publication_year,
                     instrumentation_category, numbered_subpieces, subpieces_start, notes, is_provisional,
                     instrumentation_json, composition_years_json, text_author_json, roles_json,
                     arrangements_json, cadenza_json, title_number_json,
                     catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix
                FROM pieces
                """, tx);

            await ExecAsync(conn, "DROP TABLE pieces", tx);
            await ExecAsync(conn, "ALTER TABLE pieces_new RENAME TO pieces", tx);

            // Recreate indexes EF declared (names preserved so future
            // migrations can reference them without surprise).
            await ExecAsync(conn,
                "CREATE INDEX ix_pieces_composer_catalog_sort " +
                "ON pieces (composer_id, catalog_sort_prefix, catalog_sort_number, catalog_sort_suffix)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_pieces_composer_id ON pieces (composer_id)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_pieces_composer_id_title ON pieces (composer_id, title)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_pieces_parent_piece_id_position " +
                "ON pieces (parent_piece_id, position)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_pieces_parent_version_id_position " +
                "ON pieces (parent_version_id, position)", tx);

            await using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await check.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Foreign-key check failed after recreating pieces. " +
                        "Migration aborted; the transaction will roll back.");
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        });

    /// <summary>
    /// Recreates <c>album_performers</c> with a nullable <c>album_id</c> column,
    /// plus a new CHECK constraint that guarantees every row anchors on at least
    /// one of <c>album_id</c>/<c>track_id</c>. Loose-track performers anchor on
    /// track_id; existing album-level / album-bound-track-level performers
    /// continue to anchor on album_id (with track_id null or set). Same recipe
    /// as the album_tracks migration.
    /// </summary>
    private static Task RecreateAlbumPerformersWithNullableAlbumIdAsync(
        System.Data.Common.DbConnection conn) =>
        WithForeignKeysOffAsync(conn, async () =>
        {
        await using (var tx = await conn.BeginTransactionAsync().ConfigureAwait(false))
        {
            await ExecAsync(conn, """
                CREATE TABLE album_performers_new (
                    id           INTEGER NOT NULL CONSTRAINT PK_album_performers PRIMARY KEY AUTOINCREMENT,
                    album_id     INTEGER     NULL,
                    track_id     INTEGER     NULL,
                    position     INTEGER NOT NULL,
                    person_id    INTEGER     NULL,
                    ensemble_id  INTEGER     NULL,
                    display_name TEXT        NULL,
                    role         TEXT        NULL,
                    instrument   TEXT        NULL,
                    CONSTRAINT ck_album_performers_person_xor_ensemble
                        CHECK ((person_id IS NULL) OR (ensemble_id IS NULL)),
                    CONSTRAINT ck_album_performers_has_identity
                        CHECK ((person_id IS NOT NULL) OR (ensemble_id IS NOT NULL) OR (display_name IS NOT NULL)),
                    CONSTRAINT ck_album_performers_has_owner
                        CHECK ((album_id IS NOT NULL) OR (track_id IS NOT NULL)),
                    CONSTRAINT FK_album_performers_albums_album_id
                        FOREIGN KEY (album_id)    REFERENCES albums       (id) ON DELETE CASCADE,
                    CONSTRAINT FK_album_performers_album_tracks_track_id
                        FOREIGN KEY (track_id)    REFERENCES album_tracks (id) ON DELETE CASCADE,
                    CONSTRAINT FK_album_performers_people_person_id
                        FOREIGN KEY (person_id)   REFERENCES people       (id) ON DELETE RESTRICT,
                    CONSTRAINT FK_album_performers_ensembles_ensemble_id
                        FOREIGN KEY (ensemble_id) REFERENCES ensembles    (id) ON DELETE RESTRICT
                )
                """, tx);

            await ExecAsync(conn, """
                INSERT INTO album_performers_new
                    (id, album_id, track_id, position, person_id, ensemble_id, display_name, role, instrument)
                SELECT
                     id, album_id, track_id, position, person_id, ensemble_id, display_name, role, instrument
                FROM album_performers
                """, tx);

            await ExecAsync(conn, "DROP TABLE album_performers", tx);
            await ExecAsync(conn, "ALTER TABLE album_performers_new RENAME TO album_performers", tx);

            // Indexes EF declared on the entity. Recreating with the same names
            // keeps any future migrations easy to reason about.
            await ExecAsync(conn,
                "CREATE INDEX IX_album_performers_AlbumId_TrackId_Position " +
                "ON album_performers (album_id, track_id, position)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_album_performers_PersonId " +
                "ON album_performers (person_id)", tx);
            await ExecAsync(conn,
                "CREATE INDEX IX_album_performers_EnsembleId " +
                "ON album_performers (ensemble_id)", tx);

            await using (var check = conn.CreateCommand())
            {
                check.Transaction = tx;
                check.CommandText = "PRAGMA foreign_key_check";
                await using var reader = await check.ExecuteReaderAsync().ConfigureAwait(false);
                if (await reader.ReadAsync().ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "Foreign-key check failed after recreating album_performers. " +
                        "Migration aborted; the transaction will roll back.");
                }
            }

            await tx.CommitAsync().ConfigureAwait(false);
        }
        });

    /// <summary>
    /// Runs <paramref name="body"/> with SQLite foreign-key enforcement
    /// temporarily disabled, restoring it in a <c>finally</c> block. SQLite
    /// requires <c>PRAGMA foreign_keys</c> to be toggled outside any
    /// transaction (it's not transactional itself, so toggling inside has no
    /// effect), and naïvely placing the OFF…ON pair around a transaction
    /// leaves FKs disabled if the body throws — the connection then lives on
    /// with FK enforcement silently off for any caller that reuses it (the
    /// Microsoft.Data.Sqlite connection pool is a recycling pool). Wrapping
    /// in try/finally guarantees ON runs on every exit path; the finally also
    /// catches any error from the ON command itself by surfacing it on the
    /// way out only if the body succeeded (otherwise the body's exception
    /// wins). See Rework C6.
    /// </summary>
    internal static async Task WithForeignKeysOffAsync(
        System.Data.Common.DbConnection conn, Func<Task> body)
    {
        await ExecAsync(conn, "PRAGMA foreign_keys=OFF").ConfigureAwait(false);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            // Best-effort restore. If the body threw, that exception
            // propagates; if the PRAGMA itself throws after a successful
            // body, the connection is left in a bad state but at least the
            // user sees the error rather than silently running with FKs off.
            await ExecAsync(conn, "PRAGMA foreign_keys=ON").ConfigureAwait(false);
        }
    }
}
