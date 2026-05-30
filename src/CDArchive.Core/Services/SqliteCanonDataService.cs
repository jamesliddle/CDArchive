using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

/// <summary>
/// Primary <see cref="ICanonDataService"/> implementation backed by SQLite via EF Core.
/// SQLite is the single source of truth for the runtime — Load/Save touch only
/// the database. The canonical JSON files are decoupled from the runtime
/// entirely; export/import is an explicit, user-driven operation handled by
/// <see cref="CanonDataService"/> directly (Import/Export screen,
/// <c>tools/CDArchive.Tools.SeedDb</c>).
///
/// <para>
/// On first call to any Load/Save method, <see cref="EnsureInitializedAsync"/>
/// makes sure the SQLite schema exists. It does <em>not</em> seed from JSON —
/// the schema starts empty if the database file is fresh. Initial population
/// is the seeder tool's job, run once after a clean checkout.
/// </para>
///
/// <para>
/// The wrapped <see cref="CanonDataService"/> dependency is retained solely for
/// path resolution: <see cref="ComposersFilePath"/> and friends point at where
/// the JSON files <em>would</em> live, used by the Import/Export view as the
/// default location for ad-hoc backup operations. No I/O against those paths
/// happens during normal Load/Save.
/// </para>
///
/// <para>
/// Object-identity tracking: each domain model returned by Load is registered in
/// a <see cref="ConditionalWeakTable{TKey,TValue}"/> against the row's primary
/// key. Save methods consult this map first to find the row to update, falling
/// back to composite-key matching (e.g. composer name, label+catalogue number)
/// when the model wasn't loaded by this service.
/// </para>
/// </summary>
public partial class SqliteCanonDataService : ICanonDataService
{
    private readonly IDbContextFactory<CanonDbContext> _dbFactory;
    private readonly CanonDataService _jsonService;
    private readonly ILogger<SqliteCanonDataService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private sealed class IdHandle { public long Id; }

    private readonly ConditionalWeakTable<CanonComposer, IdHandle>     _composerIds = new();
    private readonly ConditionalWeakTable<CanonPiece, IdHandle>        _pieceIds    = new();
    private readonly ConditionalWeakTable<CanonPieceVersion, IdHandle> _versionIds  = new();
    private readonly ConditionalWeakTable<CanonAlbum, IdHandle>        _albumIds    = new();
    private readonly ConditionalWeakTable<AlbumTrack, IdHandle>        _looseTrackIds = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SqliteCanonDataService(
        IDbContextFactory<CanonDbContext> dbFactory,
        CanonDataService jsonService,
        ILogger<SqliteCanonDataService>? logger = null)
    {
        _dbFactory   = dbFactory;
        _jsonService = jsonService;
        _logger      = logger ?? NullLogger<SqliteCanonDataService>.Instance;
    }

    public string ComposersFilePath    => _jsonService.ComposersFilePath;
    public string PiecesFilePath       => _jsonService.PiecesFilePath;
    public string AlbumsFilePath       => _jsonService.AlbumsFilePath;
    public string LooseTracksFilePath  => _jsonService.LooseTracksFilePath;
    public string PickListsFilePath => _jsonService.PickListsFilePath;

    // ─────────────────────────────────────────────────────────────────────────
    // Initialisation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the SQLite schema if absent. Safe to call repeatedly — the first
    /// call performs the work and subsequent calls return immediately.
    /// <para>
    /// Does <em>not</em> seed from JSON. Initial population is the seeder
    /// tool's responsibility (<c>dotnet run --project tools/CDArchive.Tools.SeedDb</c>),
    /// not the runtime data service's. Auto-seeding here would let a stale
    /// JSON file silently overwrite an empty-but-intentional database, and
    /// it's what allowed the JSON ↔ SQLite divergence we just removed.
    /// </para>
    /// </summary>
    public async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
            await db.Database.EnsureCreatedAsync().ConfigureAwait(false);
            await ApplySchemaUpgradesAsync(db).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

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

    // Composers subsystem extracted to SqliteCanonDataService.Composers.cs
    // (H1 slice 1). Public Load / Save methods + the CWT-id-update closure
    // pattern live in the partial there.

    // Pick lists subsystem extracted to SqliteCanonDataService.PickLists.cs
    // (H1 slice 2). Public Load / Save methods + the SavePickListsCoreAsync
    // transactional body + the AddStringList helper live in the partial there.

    // Pieces subsystem extracted to SqliteCanonDataService.Pieces.cs
    // (H1 slice 3 — the biggest single slice in the arc). Public Load /
    // Save methods + their internal helpers + the row->model mappers +
    // the upsert tree engine + the inner-collection Replace helpers all
    // live in the partial there.


    // Albums subsystem extracted to SqliteCanonDataService.Albums.cs
    // (H1 slice 5 — the biggest single slice in the arc). Public Load /
    // Save methods + the load-mutate-save merge engine + the row<->model
    // mappers + the singleton-album promotion (CLI migration tool) all
    // live in the partial there. Shared row-translation helpers
    // (BuildTrackPieceRef / MergePieceRefs / MergeTrackPerformers /
    // MapPerformerRow / SerializeStringList / DeserializeStringList) live
    // there too; the LooseTracks partial consumes them via partial-class
    // shared state.

    // Loose tracks subsystem extracted to SqliteCanonDataService.LooseTracks.cs
    // (H1 slice 4). Public Load / Save methods + SaveLooseTracksCoreAsync +
    // ApplyLooseTrackFields live in the partial there.


    // ─────────────────────────────────────────────────────────────────────────
    // Atomic batch save
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task SaveBatchAsync(
        List<CanonComposer>? composers = null,
        List<CanonPiece>? pieces = null,
        List<CanonAlbum>? albums = null,
        List<AlbumTrack>? looseTracks = null,
        CanonPickLists? pickLists = null)
    {
        if (composers is null && pieces is null && albums is null
            && looseTracks is null && pickLists is null)
            return;

        var __sw = Stopwatch.StartNew();
        _logger.LogInformation(
            "SaveBatch starting (composers={Composers}, pieces={Pieces}, albums={Albums}, looseTracks={LooseTracks}, pickLists={PickLists})",
            composers?.Count, pieces?.Count, albums?.Count, looseTracks?.Count, pickLists is not null);

        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        // Run each subsystem's core helper inside the shared transaction. Order
        // matters: pieces.composer_id FK requires composers to exist, and
        // album/loose-track piece refs need the piece tree current. Pick lists
        // are FK-independent but run first so that a piece save in the same
        // batch sees a freshly-renamed value already persisted (matches the
        // pick-list rename propagation flow). The Core helpers stage writes
        // via SaveChangesAsync — none commit until the single tx.CommitAsync
        // below, so any failure rolls every subsystem back. CWT id updates are
        // queued as post-commit actions so a rolled-back batch doesn't leave
        // the in-memory models pointing at ghost row ids.
        var post = new List<Action>(5);
        if (pickLists is not null)
            await SavePickListsCoreAsync(db, pickLists).ConfigureAwait(false);
        if (composers is not null)
            post.Add(await SaveComposersCoreAsync(db, composers).ConfigureAwait(false));
        if (pieces is not null)
            post.Add(await SavePiecesCoreAsync(db, pieces).ConfigureAwait(false));
        if (albums is not null)
            post.Add(await SaveAlbumsCoreAsync(db, albums).ConfigureAwait(false));
        if (looseTracks is not null)
            post.Add(await SaveLooseTracksCoreAsync(db, looseTracks).ConfigureAwait(false));

        // Pick-list rows are staged but the SaveChangesAsync that flushes them
        // only happens implicitly via the Core helpers above. If pick lists
        // were the only subsystem in this batch, the Core helpers haven't run
        // — flush explicitly so the staged rows reach the transaction.
        if (pickLists is not null && post.Count == 0)
            await db.SaveChangesAsync().ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);

        foreach (var apply in post) apply();

        _logger.LogInformation("SaveBatch completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }


}
