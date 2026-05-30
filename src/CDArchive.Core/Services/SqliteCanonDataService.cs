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


    // ─────────────────────────────────────────────────────────────────────────
    // Albums
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<CanonAlbum>> LoadAlbumsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Album loading depends on the piece tree to reconstruct each track ref's
        // SubpiecePath / VersionDescription, so we load pieces here too.
        var (_, pieceModelByRowId, versionModelByRowId, pieceRowById, versionRowById) =
            await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);

        // Resolve composer name from a piece-row id, since track refs come back
        // through the piece's hierarchy.
        var composerNameById = await db.Composers.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name)
            .ConfigureAwait(false);

        // Markers indexed by id so BuildTrackPieceRef can resolve marker FKs
        // back to display values for fallback matching on import.
        var markerRowById = await db.PieceMarkers.AsNoTracking()
            .ToDictionaryAsync(m => m.Id)
            .ConfigureAwait(false);

        var albumRows = await db.Albums
            .AsNoTracking()
            .Include(a => a.Volumes)
            .Include(a => a.Sessions)
            .Include(a => a.Performers)
            .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.PieceRefs)
            .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.Performers)
            // AsSplitQuery breaks the otherwise-Cartesian load into one SELECT
            // per Include path. Without it, EF would issue a single query with
            // multiple LEFT JOINs and produce row counts that explode multiplicatively
            // (e.g. 99 albums × 22 tracks × 3 performers × 5 piece-refs).
            // See Rework M15.
            .AsSplitQuery()
            .OrderBy(a => a.Label).ThenBy(a => a.CatalogueNumber).ThenBy(a => a.Title)
            .ToListAsync()
            .ConfigureAwait(false);

        var result = new List<CanonAlbum>(albumRows.Count);
        foreach (var ar in albumRows)
        {
            var model = MapAlbumRowToModel(ar, pieceRowById, versionRowById,
                                           pieceModelByRowId, versionModelByRowId,
                                           composerNameById, markerRowById);
            _albumIds.AddOrUpdate(model, new IdHandle { Id = ar.Id });
            result.Add(model);
        }
        return result;
    }

    private static CanonAlbum MapAlbumRowToModel(
        AlbumRow ar,
        Dictionary<long, PieceRow> pieceRowById,
        Dictionary<long, PieceVersionRow> versionRowById,
        Dictionary<long, CanonPiece> pieceModelByRowId,
        Dictionary<long, CanonPieceVersion> versionModelByRowId,
        Dictionary<long, string> composerNameById,
        Dictionary<long, PieceMarkerRow> markerRowById)
    {
        var album = new CanonAlbum
        {
            Title           = ar.Title,
            Subtitle        = ar.Subtitle,
            Label           = ar.Label,
            CatalogueNumber = ar.CatalogueNumber,
            Barcode         = ar.Barcode,
            SparsCode       = ar.SparsCode,
            IsStereo        = ar.IsStereo,
            Notes           = ar.Notes,
            ArchiveFolder   = ar.ArchiveFolder,
            IsProvisional   = ar.IsProvisional,
        };

        if (ar.Volumes.Count > 0)
        {
            album.Volumes = ar.Volumes
                .OrderBy(v => v.Number)
                .Select(v => new AlbumVolume
                {
                    Number   = v.Number,
                    Title    = v.Title,
                    Subtitle = v.Subtitle,
                })
                .ToList();
        }

        // Sessions ordered by Position. Tracks reference sessions by their
        // stable Id (AlbumTrack.SessionId).
        var orderedSessions = ar.Sessions.OrderBy(s => s.Position).ToList();
        if (orderedSessions.Count > 0)
        {
            album.Sessions = new List<RecordingSession>(orderedSessions.Count);
            for (int i = 0; i < orderedSessions.Count; i++)
            {
                var sr = orderedSessions[i];
                album.Sessions.Add(new RecordingSession
                {
                    Id        = sr.Id,
                    Dates     = sr.Dates,
                    Venue     = sr.Venue,
                    City      = sr.City,
                    Country   = sr.Country,
                    Engineers = DeserializeStringList(sr.EngineersJson),
                    Producers = DeserializeStringList(sr.ProducersJson),
                });
            }
        }

        // Volume → AlbumVolume.Number lookup so discs can map back.
        var volumeNumberById = ar.Volumes.ToDictionary(v => v.Id, v => (int?)v.Number);

        // ── Discs and tracks ─────────────────────────────────────────────────
        // The album_performers table stores both album-level credits (TrackId is
        // null) and track-level overrides (TrackId is set). The model preserves
        // that distinction: track-level performers replace the album-level list
        // when set, otherwise the track inherits.
        var albumLevelPerformers = ar.Performers
            .Where(p => !p.TrackId.HasValue)
            .OrderBy(p => p.Position)
            .ToList();
        var trackPerformersByTrackId = ar.Performers
            .Where(p => p.TrackId.HasValue)
            .GroupBy(p => p.TrackId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Position).ToList());

        if (albumLevelPerformers.Count > 0)
            album.Performers = albumLevelPerformers.Select(MapPerformerRow).ToList();

        album.Discs = new List<AlbumDisc>(ar.Discs.Count);
        foreach (var dr in ar.Discs.OrderBy(d => d.DiscNumber))
        {
            var disc = new AlbumDisc
            {
                DiscNumber   = dr.DiscNumber,
                Title        = dr.Title,
                FolderName   = dr.FolderName,
                VolumeNumber = dr.VolumeId.HasValue && volumeNumberById.TryGetValue(dr.VolumeId.Value, out var vn)
                               ? vn : null,
                Tracks       = new List<AlbumTrack>(dr.Tracks.Count),
            };

            foreach (var tr in dr.Tracks.OrderBy(t => t.TrackNumber))
            {
                var track = new AlbumTrack
                {
                    TrackNumber   = tr.TrackNumber,
                    Duration      = tr.Duration,
                    Description   = tr.Description,
                    SparsCode     = tr.SparsCode,
                    IsStereo      = tr.IsStereo,
                    IsProvisional = tr.IsProvisional,
                    FlacPath      = tr.FlacPath,
                    Mp3Path       = tr.Mp3Path,
                    SessionId     = tr.SessionId,
                };

                if (trackPerformersByTrackId.TryGetValue(tr.Id, out var tps) && tps.Count > 0)
                    track.Performers = tps.Select(MapPerformerRow).ToList();

                if (tr.PieceRefs.Count > 0)
                {
                    var refs = new List<TrackPieceRef>(tr.PieceRefs.Count);
                    foreach (var pr in tr.PieceRefs.OrderBy(r => r.Position))
                    {
                        var refModel = BuildTrackPieceRef(pr,
                            pieceRowById, versionRowById,
                            pieceModelByRowId, versionModelByRowId,
                            composerNameById, markerRowById);
                        if (refModel is not null) refs.Add(refModel);
                    }
                    if (refs.Count > 0) track.PieceRefs = refs;
                }

                disc.Tracks.Add(track);
            }

            album.Discs.Add(disc);
        }

        return album;
    }

    private static AlbumPerformer MapPerformerRow(AlbumPerformerRow r) => new()
    {
        Name       = r.DisplayName ?? "",
        Role       = r.Role,
        Instrument = r.Instrument,
    };

    private static List<string>? DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        return JsonSerializer.Deserialize<List<string>>(json, ReadOptions);
    }

    /// <summary>
    /// Reconstructs a <see cref="TrackPieceRef"/> from its row by walking up the
    /// piece hierarchy from the leaf row to its top-level piece. Each
    /// intermediate piece contributes a path entry computed via
    /// <see cref="CanonPiece.BuildSubpieceTitle"/> so that the resulting ref
    /// matches the strings the resolver indexes.
    /// <para>
    /// Set boundaries are treated as logical top levels: <see cref="PieceReferenceIndex"/>
    /// recurses into <c>form: "set"</c> containers and registers each member under
    /// its own (catalog-bearing) title, so the ref must address members directly
    /// rather than wrapping them as <c>(set-title, [member-title])</c>. Without
    /// this stop condition, the wrapper title (e.g. "Three Piano Sonatas") collides
    /// across opuses — the resolver's <c>TryAdd</c> retains only the first registrant
    /// and every other set's members silently miss.
    /// </para>
    /// </summary>
    private static TrackPieceRef? BuildTrackPieceRef(
        AlbumTrackPieceRefRow refRow,
        Dictionary<long, PieceRow> pieceRowById,
        Dictionary<long, PieceVersionRow> versionRowById,
        Dictionary<long, CanonPiece> pieceModelByRowId,
        Dictionary<long, CanonPieceVersion> versionModelByRowId,
        Dictionary<long, string> composerNameById,
        Dictionary<long, PieceMarkerRow> markerRowById)
    {
        var startWalk = WalkUpToTop(refRow.PieceId, pieceRowById, versionRowById, pieceModelByRowId);
        if (startWalk is null) return null;
        var (path, topRow, versionDescription) = startWalk.Value;

        if (!pieceModelByRowId.TryGetValue(topRow.Id, out var topModel)) return null;

        var pieceTitle = !string.IsNullOrEmpty(topModel.Title)
            ? topModel.Title!
            : topModel.DisplayTitleShort;

        var composer = topModel.Composer;
        if (string.IsNullOrEmpty(composer))
            composerNameById.TryGetValue(topRow.ComposerId, out composer);

        // If the ref carries a VersionId but the walk-up never crossed a
        // parent_version_id, the leaf is the top-level piece itself and we
        // still want the version label.
        if (versionDescription is null && refRow.VersionId.HasValue &&
            versionRowById.TryGetValue(refRow.VersionId.Value, out var directVer))
        {
            versionDescription = directVer.Description;
        }

        // End-piece path for range refs — same walk, different leaf. We don't
        // re-derive the version description from the end side because both
        // ends must share the same version (validated at save time).
        List<string>? endPath = null;
        if (refRow.EndPieceId is { } endId)
        {
            var endWalk = WalkUpToTop(endId, pieceRowById, versionRowById, pieceModelByRowId);
            if (endWalk is { } e) endPath = e.Path.Count > 0 ? e.Path : null;
        }

        var trackRef = new TrackPieceRef
        {
            Composer           = composer ?? "",
            PieceTitle         = pieceTitle,
            SubpiecePath       = path.Count > 0 ? path : null,
            EndSubpiecePath    = endPath,
            VersionDescription = versionDescription,
            DisplayLabel       = refRow.DisplayLabel,
            StartMarker        = BuildMarkerReference(refRow.StartMarkerId, markerRowById),
            EndMarker          = BuildMarkerReference(refRow.EndMarkerId,   markerRowById),
        };
        return trackRef;
    }

    /// <summary>
    /// Walks up from a leaf piece row to its effective top piece (stopping
    /// at set boundaries, the same rule as the cross-composer fix), accumulating
    /// the subpiece path along the way and recording any version description
    /// crossed. Shared between the start- and end-of-range walks in
    /// <see cref="BuildTrackPieceRef"/>.
    /// </summary>
    private static (List<string> Path, PieceRow Top, string? VersionDescription)? WalkUpToTop(
        long leafPieceId,
        Dictionary<long, PieceRow> pieceRowById,
        Dictionary<long, PieceVersionRow> versionRowById,
        Dictionary<long, CanonPiece> pieceModelByRowId)
    {
        if (!pieceRowById.TryGetValue(leafPieceId, out var leafRow)) return null;

        var path = new List<string>();
        string? versionDescription = null;
        var current = leafRow;

        while (current.ParentPieceId.HasValue || current.ParentVersionId.HasValue)
        {
            PieceRow? parentPieceRow = null;
            PieceVersionRow? parentVerRow = null;
            if (current.ParentPieceId.HasValue)
            {
                if (!pieceRowById.TryGetValue(current.ParentPieceId.Value, out parentPieceRow))
                    return null;
                if (string.Equals(parentPieceRow.Form, "Set", StringComparison.OrdinalIgnoreCase))
                    break;
            }
            else
            {
                if (!versionRowById.TryGetValue(current.ParentVersionId!.Value, out parentVerRow))
                    return null;
            }

            bool parentNumbered = parentPieceRow is not null
                ? ComputeNumberedDefault(parentPieceRow.NumberedSubpieces, parentPieceRow.InstrumentationCategory)
                : ComputeNumberedDefault(parentVerRow!.NumberedSubpieces, parentVerRow.InstrumentationCategory);

            if (pieceModelByRowId.TryGetValue(current.Id, out var currentModel))
                path.Insert(0, currentModel.BuildSubpieceTitle(parentNumbered));

            if (parentPieceRow is not null)
            {
                current = parentPieceRow;
            }
            else
            {
                versionDescription ??= parentVerRow!.Description;
                if (!pieceRowById.TryGetValue(parentVerRow!.PieceId, out var owningPiece)) return null;
                current = owningPiece;
            }
        }

        return (path, current, versionDescription);
    }

    /// <summary>
    /// Builds a <see cref="MarkerReference"/> from a marker-row id, copying
    /// kind/value/bar-number as fallback fields so a future re-import without
    /// the row context can still resolve the ref by content matching.
    /// </summary>
    private static MarkerReference? BuildMarkerReference(
        long? markerId, Dictionary<long, PieceMarkerRow> markerRowById)
    {
        if (markerId is not { } id) return null;
        if (!markerRowById.TryGetValue(id, out var row)) return null;
        return new MarkerReference
        {
            Id        = row.Id,
            Kind      = row.Kind,
            Value     = row.Value,
            BarNumber = row.BarNumber,
        };
    }

    private static bool ComputeNumberedDefault(bool? explicitFlag, string? category) =>
        explicitFlag ?? (!string.IsNullOrEmpty(category) &&
            !string.Equals(category, "Opera", StringComparison.OrdinalIgnoreCase));

    public async Task SaveAlbumsAsync(List<CanonAlbum> albums)
    {
        var __sw = Stopwatch.StartNew();
        _logger.LogInformation("SaveAlbums starting ({Count} input)", albums.Count);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        var apply = await SaveAlbumsCoreAsync(db, albums).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
        apply();

        _logger.LogInformation("SaveAlbums completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Transactional body of <see cref="SaveAlbumsAsync"/>. The caller owns the
    /// <see cref="CanonDbContext"/> and the transaction — this helper performs
    /// the load-mutate-save merge and a single <c>SaveChangesAsync</c>, but
    /// does not commit. See <see cref="SaveComposersCoreAsync"/> for the contract.
    /// </summary>
    private async Task<Action> SaveAlbumsCoreAsync(CanonDbContext db, List<CanonAlbum> albums)
    {
        // Resolve track-piece refs against the live piece tree.
        var (currentPieces, pieceModelByRowId, versionModelByRowId, _, _) =
            await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);
        var rowIdByPieceModel = new Dictionary<CanonPiece, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in pieceModelByRowId) rowIdByPieceModel[m] = id;
        var rowIdByVersionModel = new Dictionary<CanonPieceVersion, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in versionModelByRowId) rowIdByVersionModel[m] = id;

        // Throwaway resolver — registerAsCurrent:false so the save path doesn't
        // steal Current from the live index. Pre-fix every album save left
        // every HitCountBadgeConverter reading 0 hits until the post-save
        // RebuildContainers ran. See Rework H7.
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(currentPieces);

        // Load-mutate-save. The previous design deleted every matched album's
        // row outright and reinserted it from scratch; one constraint violation
        // anywhere in the input could (and did) wipe the whole albums table.
        // We now resolve each input album to its target row via a cheap
        // identity-only projection, load the FULL graph for matched rows only,
        // merge in place by natural key at every level (UPDATE for matched
        // children, INSERT for new, DELETE for orphans), and rely on SQLite's
        // album→child Cascade FKs to take down orphans without loading them.
        // Row IDs survive unchanged content; a constraint failure rolls back
        // via the caller's transaction without touching unrelated rows.

        // Step 1 — cheap projection. ~5 columns × all-album rows; the heavy
        // child-collection load comes later and only for matched rows.
        var existingMeta = await db.Albums.AsNoTracking()
            .Select(a => new { a.Id, a.Label, a.CatalogueNumber, a.Title, a.Subtitle })
            .ToListAsync()
            .ConfigureAwait(false);

        // Mirror CanonAlbum.IdentityKey: composite over (Label, CatalogueNumber,
        // Title, Subtitle). Albums without Label / CatalogueNumber (Böhm
        // Beethoven, Bernstein Mahler, …) still get a stable key from
        // Title+Subtitle so save-time dedup catches them after the editor's
        // JSON-clone round-trip wipes the CWT identity.
        var existingByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in existingMeta)
        {
            var key = BuildAlbumIdentityKey(m.Label, m.CatalogueNumber, m.Title, m.Subtitle);
            if (key is not null) existingByKey[key] = m.Id;
        }
        var allExistingIds = existingMeta.Select(m => m.Id).ToHashSet();

        // Step 2 — resolve each input album to a target row id, via CWT
        // identity or the IdentityKey fallback. Albums with no match are
        // inserts.
        var matchedRowIdByModel = new Dictionary<CanonAlbum, long>(ReferenceEqualityComparer.Instance);
        var matchedExistingRowIds = new HashSet<long>();
        foreach (var album in albums)
        {
            long? rowId = null;
            if (_albumIds.TryGetValue(album, out var handle) && allExistingIds.Contains(handle.Id))
                rowId = handle.Id;
            else if (album.IdentityKey is { } key && existingByKey.TryGetValue(key, out var keyMatchId))
                rowId = keyMatchId;

            if (rowId is not null)
            {
                matchedRowIdByModel[album] = rowId.Value;
                matchedExistingRowIds.Add(rowId.Value);
            }
        }

        // Step 3 — narrow Include load. Only matched rows need their full
        // graph (we're about to mutate them in place). AsSplitQuery defeats
        // the Cartesian explosion the multiple ThenIncludes would otherwise
        // produce in a single SQL query — at 3,000+ CDs the unified-query
        // approach is the multi-second hang C11 was about.
        var matchedRows = new Dictionary<long, AlbumRow>(matchedExistingRowIds.Count);
        if (matchedExistingRowIds.Count > 0)
        {
            var loaded = await db.Albums
                .Where(a => matchedExistingRowIds.Contains(a.Id))
                .Include(a => a.Volumes)
                .Include(a => a.Sessions)
                .Include(a => a.Performers)
                .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.PieceRefs)
                .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.Performers)
                .AsSplitQuery()
                .ToListAsync()
                .ConfigureAwait(false);
            foreach (var r in loaded) matchedRows[r.Id] = r;
        }

        // Step 4 — orphan deletes. With every album→child FK declared
        // OnDelete:Cascade (volumes, discs, tracks→piece-refs/performers,
        // album-level performers, sessions), a stub-attach + Remove on the
        // album row is sufficient: SQLite's FK cascade handles every
        // descendant without us loading any of it. EF doesn't need the
        // children in its change tracker because the database does the work.
        foreach (var orphanId in allExistingIds)
        {
            if (matchedExistingRowIds.Contains(orphanId)) continue;
            var stub = new AlbumRow { Id = orphanId };
            db.Albums.Attach(stub);
            db.Albums.Remove(stub);
        }

        var inserted = new List<(CanonAlbum, AlbumRow)>();
        var matched = new Dictionary<CanonAlbum, AlbumRow>(ReferenceEqualityComparer.Instance);
        // H21: capture the position→sessionRow map per album so we can
        // back-propagate the freshly-allocated row Ids into the in-memory
        // model after SaveChanges flushes. Without this the model's
        // RecordingSession.Id stays 0 and AlbumTrack.SessionId stays null for
        // sessions added in-editor — the next operation (e.g. AlbumEditor
        // reopening) JSON-clones a track that points at nothing.
        var sessionMapsByAlbum = new Dictionary<CanonAlbum, Dictionary<int, AlbumSessionRow>>(ReferenceEqualityComparer.Instance);
        foreach (var album in albums)
        {
            if (matchedRowIdByModel.TryGetValue(album, out var rowId) &&
                matchedRows.TryGetValue(rowId, out var row))
            {
                var sessionMap = MergeAlbumIntoRow(album, row, resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                matched[album] = row;
                sessionMapsByAlbum[album] = sessionMap;
            }
            else
            {
                var (newRow, sessionMap) = MapAlbumModelToRow(album, resolver, rowIdByPieceModel, rowIdByVersionModel);
                db.Albums.Add(newRow);
                inserted.Add((album, newRow));
                sessionMapsByAlbum[album] = sessionMap;
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // H21 back-propagation: now that every AlbumSessionRow has an Id
        // (assigned during SaveChanges), copy those Ids into the in-memory
        // model so subsequent operations see SessionId instead of the stale
        // SessionIndex transient handle.
        foreach (var (album, sessionMap) in sessionMapsByAlbum)
            BackPropagateSessionIds(album, sessionMap);

        return () =>
        {
            foreach (var (m, r) in inserted)
            {
                if (_albumIds.TryGetValue(m, out var h)) h.Id = r.Id;
                else _albumIds.AddOrUpdate(m, new IdHandle { Id = r.Id });
            }
            // Matched (in-place) rows already carry their stable Id; the CWT mapping
            // is only set up at Load time, so refresh it for any model that hit the
            // IdentityKey fallback path.
            foreach (var (model, row) in matched)
            {
                if (_albumIds.TryGetValue(model, out var h)) h.Id = row.Id;
                else _albumIds.AddOrUpdate(model, new IdHandle { Id = row.Id });
            }
        };
    }

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

    // ─────────────────────────────────────────────────────────────────────────
    // Singleton-album promotion (one-shot migration of synthetic single-track
    // albums to loose tracks, run from the seeder CLI's --promote-loose-tracks
    // flag). Identifies wrapper albums via the heuristic in the docstring on
    // PromoteSingletonAlbumsToLooseTracksAsync; the actual mutation is done as
    // raw SQL in one transaction to avoid EF's orphan-tracking maze when a
    // cascade-deleted parent's child needs to survive.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One row in the result of <see cref="PromoteSingletonAlbumsToLooseTracksAsync"/>:
    /// an album that didn't match the heuristic, paired with the reason.
    /// </summary>
    public record SingletonPromoteSkipped(long AlbumId, string? Title, string Reason);

    public record SingletonPromoteResult(
        int AlbumsScanned,
        int AlbumsPromoted,
        IReadOnlyList<SingletonPromoteSkipped> Skipped);

    /// <summary>
    /// Migrates synthetic single-track wrapper albums to loose tracks. An album
    /// qualifies when all of these hold:
    /// <list type="bullet">
    ///   <item>exactly 1 disc containing exactly 1 track</item>
    ///   <item>no Volumes, no Sessions</item>
    ///   <item>no Label, no CatalogueNumber, no Barcode, no ArchiveFolder</item>
    ///   <item>the track itself has no track-level Performers (those would
    ///         suggest a manual override on a curated album)</item>
    /// </list>
    /// <para>For each qualifying album:</para>
    /// <list type="number">
    ///   <item>Re-anchor any album-level Performers to the track
    ///         (<c>album_id=NULL, track_id=&lt;track_id&gt;</c>) so they survive
    ///         the album delete.</item>
    ///   <item>Inherit album-level <c>SparsCode</c> / <c>IsStereo</c> into the
    ///         track when the track's own value is null.</item>
    ///   <item>Set the track's <c>disc_id=NULL</c>, <c>track_number=0</c>,
    ///         <c>session_id=NULL</c> — the loose-track shape.</item>
    ///   <item>Delete the disc and album rows.</item>
    /// </list>
    /// All mutation runs in one transaction. Set <paramref name="dryRun"/> to
    /// true to scan + report without changing anything.
    /// </summary>
    public async Task<SingletonPromoteResult> PromoteSingletonAlbumsToLooseTracksAsync(bool dryRun)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rows = await db.Albums
            .Include(a => a.Volumes)
            .Include(a => a.Sessions)
            .Include(a => a.Performers)
            .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.Performers)
            .ToListAsync()
            .ConfigureAwait(false);

        var skipped    = new List<SingletonPromoteSkipped>();
        var candidates = new List<(AlbumRow Album, AlbumDiscRow Disc, AlbumTrackRow Track)>();

        foreach (var album in rows)
        {
            var reason = ClassifyForPromotion(album);
            if (reason is not null)
            {
                skipped.Add(new SingletonPromoteSkipped(album.Id, album.Title, reason));
                continue;
            }
            var disc  = album.Discs[0];
            var track = disc.Tracks[0];
            candidates.Add((album, disc, track));
        }

        if (dryRun || candidates.Count == 0)
            return new SingletonPromoteResult(rows.Count, candidates.Count, skipped);

        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync().ConfigureAwait(false);

        await using var tx = await conn.BeginTransactionAsync().ConfigureAwait(false);
        foreach (var (album, disc, track) in candidates)
        {
            // 1. Re-anchor album-level performers (those rows have track_id NULL)
            //    onto the track so they survive the album delete's cascade.
            await ExecParamAsync(conn, tx,
                "UPDATE album_performers SET album_id = NULL, track_id = @trackId " +
                "WHERE album_id = @albumId AND track_id IS NULL",
                ("@trackId", track.Id), ("@albumId", album.Id));

            // 2. Inherit SparsCode / IsStereo from the album when the track's
            //    are null — those were album-level on the synthetic wrapper and
            //    have nowhere else to live once the album is gone.
            var newSpars  = track.SparsCode ?? album.SparsCode;
            var newStereo = track.IsStereo  ?? album.IsStereo;

            // 3. Detach the track: disc_id null (loose), track_number 0 (loose
            //    sentinel), session_id null (no album sessions anymore).
            await ExecParamAsync(conn, tx,
                "UPDATE album_tracks SET disc_id = NULL, track_number = 0, session_id = NULL, " +
                "spars_code = @spars, is_stereo = @stereo WHERE id = @trackId",
                ("@spars",   (object?)newSpars ?? DBNull.Value),
                ("@stereo",  newStereo.HasValue ? (object)(newStereo.Value ? 1 : 0) : DBNull.Value),
                ("@trackId", track.Id));

            // 4. Delete the disc and album. Cascade-deletes any remaining
            //    volumes / sessions / track-level-only performers; performers
            //    we re-anchored in step 1 are no longer FK-linked to the album.
            await ExecParamAsync(conn, tx,
                "DELETE FROM album_discs WHERE id = @discId", ("@discId", disc.Id));
            await ExecParamAsync(conn, tx,
                "DELETE FROM albums WHERE id = @albumId", ("@albumId", album.Id));
        }
        await tx.CommitAsync().ConfigureAwait(false);

        return new SingletonPromoteResult(rows.Count, candidates.Count, skipped);
    }

    private static string? ClassifyForPromotion(AlbumRow album)
    {
        if (album.Discs.Count != 1)                                        return $"has {album.Discs.Count} disc(s)";
        if (album.Discs[0].Tracks.Count != 1)                              return $"disc has {album.Discs[0].Tracks.Count} track(s)";
        if (album.Volumes.Count > 0)                                       return "has volumes";
        if (album.Sessions.Count > 0)                                      return "has sessions";
        if (!string.IsNullOrWhiteSpace(album.Label))                       return $"has Label='{album.Label}'";
        if (!string.IsNullOrWhiteSpace(album.CatalogueNumber))             return $"has CatalogueNumber='{album.CatalogueNumber}'";
        if (!string.IsNullOrWhiteSpace(album.Barcode))                     return "has Barcode";
        if (!string.IsNullOrWhiteSpace(album.ArchiveFolder))               return "has ArchiveFolder";
        if (album.Discs[0].Tracks[0].Performers.Count > 0)                 return "track has track-level Performers (manual override?)";
        return null;
    }

    private static async Task ExecParamAsync(
        System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Album merge helpers (load-mutate-save path)
    //
    // Each helper diffs an input child collection against the rows currently
    // attached to the parent row (loaded via Include in SaveAlbumsAsync), and
    // applies the minimal change: update matched rows in place, insert new
    // ones, db.Remove() orphans. Natural keys (Number, DiscNumber, TrackNumber,
    // Position) come straight from the schema's existing UNIQUE indexes.
    // ─────────────────────────────────────────────────────────────────────────

    private static Dictionary<int, AlbumSessionRow> MergeAlbumIntoRow(
        CanonAlbum album,
        AlbumRow row,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel,
        CanonDbContext db)
    {
        row.Title           = album.Title;
        row.Subtitle        = album.Subtitle;
        row.Label           = album.Label;
        row.CatalogueNumber = album.CatalogueNumber;
        row.Barcode         = album.Barcode;
        row.SparsCode       = album.SparsCode;
        row.IsStereo        = album.IsStereo;
        row.Notes           = album.Notes;
        row.ArchiveFolder   = album.ArchiveFolder;
        row.IsProvisional   = album.IsProvisional;

        // Volumes and sessions get planned first (returning the row that each
        // input slot should resolve to) but their orphan deletions are deferred
        // until after disc/track rewiring, so disc.Volume / track.Session FKs
        // are pointing at the new survivors by the time the deletes fire. Avoids
        // FK Restrict failures (volumes) and surprise SetNull side-effects
        // (sessions).
        var (volumeMap, orphanVolumes) = PlanVolumes(album.Volumes, row);
        var (sessionMap, sessionMapById, orphanSessions) = PlanSessions(album.Sessions, row);

        MergeAlbumLevelPerformers(album.Performers, row, db);

        MergeDiscs(album.Discs, row, volumeMap, sessionMap, sessionMapById,
                   resolver, rowIdByPieceModel, rowIdByVersionModel, db);

        foreach (var v in orphanVolumes) db.AlbumVolumes.Remove(v);
        foreach (var s in orphanSessions) db.AlbumSessions.Remove(s);

        // Return the position→row map so SaveAlbumsCoreAsync can back-propagate
        // freshly-allocated session Ids into the in-memory model after
        // SaveChangesAsync flushes (H21 transient handle cleanup).
        return sessionMap;
    }

    /// <summary>
    /// Plans the volume merge for an album. Returns (map keyed by Volume Number
    /// to either the existing row updated in place or a newly attached row,
    /// list of existing volume rows to delete after disc rewiring).
    /// </summary>
    private static (Dictionary<int, AlbumVolumeRow> Map, List<AlbumVolumeRow> Orphans)
        PlanVolumes(List<AlbumVolume>? input, AlbumRow row)
    {
        var existing = row.Volumes.ToList();
        var existingByNumber = existing.ToDictionary(v => v.Number);
        var map = new Dictionary<int, AlbumVolumeRow>();
        var matched = new HashSet<long>();

        if (input is { Count: > 0 })
        {
            foreach (var v in input)
            {
                if (existingByNumber.TryGetValue(v.Number, out var er))
                {
                    er.Title    = v.Title;
                    er.Subtitle = v.Subtitle;
                    map[v.Number] = er;
                    matched.Add(er.Id);
                }
                else
                {
                    var fresh = new AlbumVolumeRow
                    {
                        Number   = v.Number,
                        Title    = v.Title,
                        Subtitle = v.Subtitle,
                    };
                    row.Volumes.Add(fresh);
                    map[v.Number] = fresh;
                }
            }
        }

        var orphans = existing.Where(v => v.Id != 0 && !matched.Contains(v.Id)).ToList();
        return (map, orphans);
    }

    /// <summary>
    /// Plans the session merge. Sessions are positional — input index becomes
    /// the schema's <c>Position</c> column. Returns (map keyed by input index,
    /// list of existing rows to delete after track rewiring).
    /// </summary>
    private static (Dictionary<int, AlbumSessionRow> Map, Dictionary<long, AlbumSessionRow> MapById, List<AlbumSessionRow> Orphans)
        PlanSessions(List<RecordingSession>? input, AlbumRow row)
    {
        var existing = row.Sessions.ToList();
        var existingByPosition = existing.ToDictionary(s => s.Position);
        var map = new Dictionary<int, AlbumSessionRow>();
        // H21 slice 1: by-stable-Id lookup so tracks carrying SessionId can
        // resolve directly without the positional dance. Populated for every
        // matched-or-created row whose Id is known (matched rows from the
        // existing DB load; fresh rows after the parent SaveChangesAsync would
        // also have Ids, but tracks added in the same transaction reference
        // them via SessionIndex anyway — the SessionId path covers the
        // common edit-an-existing-album case).
        var mapById = new Dictionary<long, AlbumSessionRow>();
        var matched = new HashSet<long>();

        if (input is { Count: > 0 })
        {
            for (int i = 0; i < input.Count; i++)
            {
                var s = input[i];
                if (existingByPosition.TryGetValue(i, out var er))
                {
                    er.Dates         = s.Dates;
                    er.Venue         = s.Venue;
                    er.City          = s.City;
                    er.Country       = s.Country;
                    er.EngineersJson = SerializeStringList(s.Engineers);
                    er.ProducersJson = SerializeStringList(s.Producers);
                    map[i] = er;
                    matched.Add(er.Id);
                    if (er.Id != 0) mapById[er.Id] = er;
                }
                else
                {
                    var fresh = new AlbumSessionRow
                    {
                        Position      = i,
                        Dates         = s.Dates,
                        Venue         = s.Venue,
                        City          = s.City,
                        Country       = s.Country,
                        EngineersJson = SerializeStringList(s.Engineers),
                        ProducersJson = SerializeStringList(s.Producers),
                    };
                    row.Sessions.Add(fresh);
                    map[i] = fresh;
                }
                // Honour the input model's stable Id even when its position is
                // new: lets a track with SessionId resolve to a fresh session
                // when the user reordered before adding.
                if (s.Id != 0) mapById[s.Id] = map[i];
            }
        }

        var orphans = existing.Where(s => s.Id != 0 && !matched.Contains(s.Id)).ToList();
        return (map, mapById, orphans);
    }

    /// <summary>
    /// Album-level performers are positional and live in <c>row.Performers</c>
    /// filtered to <c>TrackId IS NULL</c>. Match by Position == input index.
    /// </summary>
    private static void MergeAlbumLevelPerformers(
        List<AlbumPerformer>? input, AlbumRow row, CanonDbContext db)
    {
        var existing = row.Performers
            .Where(p => p.TrackId == null)
            .OrderBy(p => p.Position)
            .ToList();
        var existingByPosition = existing.ToDictionary(p => p.Position);
        var matched = new HashSet<long>();

        if (input is { Count: > 0 })
        {
            for (int i = 0; i < input.Count; i++)
            {
                var p = input[i];
                if (existingByPosition.TryGetValue(i, out var er))
                {
                    er.DisplayName = p.Name;
                    er.Role        = p.Role;
                    er.Instrument  = p.Instrument;
                    matched.Add(er.Id);
                }
                else
                {
                    var fresh = MapPerformerModelToRow(p, i);
                    fresh.Album = row;
                    row.Performers.Add(fresh);
                }
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumPerformers.Remove(orphan);
    }

    /// <summary>
    /// Discs match by DiscNumber within the album. Each matched disc gets its
    /// scalar fields updated, its Volume FK rewired through <paramref name="volumeMap"/>,
    /// and its Tracks merged. Unmatched input discs insert; existing discs with
    /// no input match delete (cascading tracks / piece-refs / per-track performers).
    /// </summary>
    private static void MergeDiscs(
        List<AlbumDisc> input,
        AlbumRow row,
        Dictionary<int, AlbumVolumeRow> volumeMap,
        Dictionary<int, AlbumSessionRow> sessionMap,
        Dictionary<long, AlbumSessionRow> sessionMapById,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel,
        CanonDbContext db)
    {
        var existing = row.Discs.ToList();
        var existingByNumber = existing.ToDictionary(d => d.DiscNumber);
        var matched = new HashSet<long>();

        foreach (var inputDisc in input)
        {
            if (existingByNumber.TryGetValue(inputDisc.DiscNumber, out var dr))
            {
                dr.Title      = inputDisc.Title;
                dr.FolderName = inputDisc.FolderName;
                dr.Volume     = (inputDisc.VolumeNumber is int vn && volumeMap.TryGetValue(vn, out var vRow))
                                    ? vRow : null;
                MergeTracks(inputDisc.Tracks, dr, sessionMap, sessionMapById,
                            resolver, rowIdByPieceModel, rowIdByVersionModel, row, db);
                matched.Add(dr.Id);
            }
            else
            {
                var fresh = new AlbumDiscRow
                {
                    DiscNumber = inputDisc.DiscNumber,
                    Title      = inputDisc.Title,
                    FolderName = inputDisc.FolderName,
                    Volume     = (inputDisc.VolumeNumber is int vn && volumeMap.TryGetValue(vn, out var vRow))
                                    ? vRow : null,
                };
                row.Discs.Add(fresh);
                // For a brand-new disc all input tracks are also new — the
                // merge path runs identically against an empty existing set.
                MergeTracks(inputDisc.Tracks, fresh, sessionMap, sessionMapById,
                            resolver, rowIdByPieceModel, rowIdByVersionModel, row, db);
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumDiscs.Remove(orphan);
    }

    /// <summary>
    /// Tracks match by TrackNumber within their disc. Mirrors the disc merge:
    /// scalar fields, Session FK via <paramref name="sessionMap"/>, then
    /// recurses into per-track piece-refs and per-track performers.
    /// </summary>
    private static void MergeTracks(
        List<AlbumTrack> input,
        AlbumDiscRow disc,
        Dictionary<int, AlbumSessionRow> sessionMap,
        Dictionary<long, AlbumSessionRow> sessionMapById,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel,
        AlbumRow albumRow,
        CanonDbContext db)
    {
        var existing = disc.Tracks.ToList();
        var existingByNumber = existing.ToDictionary(t => t.TrackNumber);
        var matched = new HashSet<long>();

        foreach (var inputTrack in input)
        {
            // H21 slice 1: prefer the stable SessionId reference when present;
            // fall back to the legacy positional SessionIndex for pre-H21
            // models / freshly-added sessions whose Id isn't assigned yet.
            var resolvedSession = ResolveSessionRow(inputTrack, sessionMap, sessionMapById);

            if (existingByNumber.TryGetValue(inputTrack.TrackNumber, out var tr))
            {
                tr.Duration      = inputTrack.Duration;
                tr.Description   = inputTrack.Description;
                tr.SparsCode     = inputTrack.SparsCode;
                tr.IsStereo      = inputTrack.IsStereo;
                tr.IsProvisional = inputTrack.IsProvisional;
                tr.FlacPath      = inputTrack.FlacPath;
                tr.Mp3Path       = inputTrack.Mp3Path;
                tr.Session       = resolvedSession;

                MergePieceRefs(inputTrack.PieceRefs, tr,
                               resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                MergeTrackPerformers(inputTrack.Performers, tr, albumRow, db);
                matched.Add(tr.Id);
            }
            else
            {
                var fresh = new AlbumTrackRow
                {
                    TrackNumber   = inputTrack.TrackNumber,
                    Duration      = inputTrack.Duration,
                    Description   = inputTrack.Description,
                    SparsCode     = inputTrack.SparsCode,
                    IsStereo      = inputTrack.IsStereo,
                    IsProvisional = inputTrack.IsProvisional,
                    FlacPath      = inputTrack.FlacPath,
                    Mp3Path       = inputTrack.Mp3Path,
                    Session       = resolvedSession,
                };
                disc.Tracks.Add(fresh);
                MergePieceRefs(inputTrack.PieceRefs, fresh,
                               resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                MergeTrackPerformers(inputTrack.Performers, fresh, albumRow, db);
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumTracks.Remove(orphan);
    }

    /// <summary>
    /// H21 back-propagation: after <c>SaveChangesAsync</c> flushes (allocating
    /// row Ids for any newly-inserted sessions), copy those Ids back into the
    /// in-memory model so subsequent operations see the persistent reference.
    /// Without this, a session added in the AlbumEditor in the same app
    /// session as the save retains <c>RecordingSession.Id = 0</c>, and tracks
    /// pointing at it via the transient positional <see cref="AlbumTrack.SessionIndex"/>
    /// handle have <see cref="AlbumTrack.SessionId"/> = null. Re-opening the
    /// AlbumEditor JSON-clones such a track, losing the session reference
    /// entirely (the positional handle isn't serialized).
    /// </summary>
    private static void BackPropagateSessionIds(
        CanonAlbum album,
        Dictionary<int, AlbumSessionRow> sessionMap)
    {
        if (album.Sessions is not { Count: > 0 }) return;

        // Step 1: assign newly-allocated row Ids onto the in-memory
        // RecordingSession instances.
        for (int i = 0; i < album.Sessions.Count; i++)
        {
            if (album.Sessions[i].Id == 0 &&
                sessionMap.TryGetValue(i, out var row) && row.Id != 0)
            {
                album.Sessions[i].Id = row.Id;
            }
        }

        // Step 2: walk all tracks. Where the track was using the SessionIndex
        // transient handle (SessionId null, SessionIndex set), look up the
        // session's now-allocated Id and populate SessionId. Clear the
        // transient handle since SessionId is now authoritative.
        foreach (var disc in album.Discs)
        foreach (var track in disc.Tracks)
        {
            if (track.SessionId is null && track.SessionIndex is int si &&
                si >= 0 && si < album.Sessions.Count &&
                album.Sessions[si].Id != 0)
            {
                track.SessionId    = album.Sessions[si].Id;
                track.SessionIndex = null;
            }
        }
    }

    /// <summary>
    /// H21: pick the right <see cref="AlbumSessionRow"/> for a track being
    /// saved. Priority order:
    ///   1. <see cref="AlbumTrack.SessionId"/> against the stable-Id map
    ///      (post-H21 path — survives session reorders).
    ///   2. <see cref="AlbumTrack.SessionIndex"/> against the positional map
    ///      (legacy path — pre-H21 snapshots and freshly-added sessions whose
    ///      Id isn't yet allocated by SQLite).
    ///   3. null when neither resolves.
    /// </summary>
    private static AlbumSessionRow? ResolveSessionRow(
        AlbumTrack inputTrack,
        Dictionary<int, AlbumSessionRow> sessionMap,
        Dictionary<long, AlbumSessionRow> sessionMapById)
    {
        if (inputTrack.SessionId is long sid && sessionMapById.TryGetValue(sid, out var byId))
            return byId;
        if (inputTrack.SessionIndex is int si && sessionMap.TryGetValue(si, out var byPos))
            return byPos;
        return null;
    }

    /// <summary>
    /// Track-level performers are positional within the track. Live in
    /// <c>track.Performers</c> with TrackId set; album-bound tracks also get
    /// flat-listed in <c>album.Performers</c>. Pass <paramref name="albumRow"/>
    /// as null for loose tracks — the credit anchors on the track only, with
    /// <c>album_id NULL</c> (the <c>ck_album_performers_has_owner</c> CHECK
    /// constraint is satisfied because track_id is set).
    /// </summary>
    private static void MergeTrackPerformers(
        List<AlbumPerformer>? input, AlbumTrackRow track, AlbumRow? albumRow, CanonDbContext db)
    {
        var existing = track.Performers.OrderBy(p => p.Position).ToList();
        var existingByPosition = existing.ToDictionary(p => p.Position);
        var matched = new HashSet<long>();

        if (input is { Count: > 0 })
        {
            for (int i = 0; i < input.Count; i++)
            {
                var p = input[i];
                if (existingByPosition.TryGetValue(i, out var er))
                {
                    er.DisplayName = p.Name;
                    er.Role        = p.Role;
                    er.Instrument  = p.Instrument;
                    matched.Add(er.Id);
                }
                else
                {
                    var fresh = MapPerformerModelToRow(p, i);
                    fresh.Track = track;
                    track.Performers.Add(fresh);
                    if (albumRow is not null)
                    {
                        fresh.Album = albumRow;
                        albumRow.Performers.Add(fresh);
                    }
                }
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumPerformers.Remove(orphan);
    }

    /// <summary>
    /// Piece-refs are positional within the track. The natural key is
    /// <c>Position</c>; content is the resolved FK bundle from
    /// <see cref="ResolvePieceRef"/>. Unresolvable refs are skipped (matches the
    /// behavior of the original delete-and-rebuild path).
    /// </summary>
    private static void MergePieceRefs(
        List<TrackPieceRef>? input,
        AlbumTrackRow track,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel,
        CanonDbContext db)
    {
        var existing = track.PieceRefs.OrderBy(r => r.Position).ToList();
        var existingByPosition = existing.ToDictionary(r => r.Position);
        var matched = new HashSet<long>();

        int slot = 0;
        if (input is { Count: > 0 })
        {
            foreach (var pieceRef in input)
            {
                var resolution = ResolvePieceRef(pieceRef, resolver,
                                                 rowIdByPieceModel, rowIdByVersionModel);
                if (resolution is null) continue;
                var r = resolution.Value;

                if (existingByPosition.TryGetValue(slot, out var er))
                {
                    er.PieceId       = r.PieceId;
                    er.VersionId     = r.VersionId;
                    er.EndPieceId    = r.EndPieceId;
                    er.StartMarkerId = r.StartMarkerId;
                    er.EndMarkerId   = r.EndMarkerId;
                    er.DisplayLabel  = r.DisplayLabel;
                    matched.Add(er.Id);
                }
                else
                {
                    track.PieceRefs.Add(new AlbumTrackPieceRefRow
                    {
                        Position      = slot,
                        PieceId       = r.PieceId,
                        VersionId     = r.VersionId,
                        EndPieceId    = r.EndPieceId,
                        StartMarkerId = r.StartMarkerId,
                        EndMarkerId   = r.EndMarkerId,
                        DisplayLabel  = r.DisplayLabel,
                    });
                }
                slot++;
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumTrackPieceRefs.Remove(orphan);
    }

    /// <summary>
    /// Resolves a <see cref="TrackPieceRef"/> against the live piece tree,
    /// returning the row-id bundle to write into an <see cref="AlbumTrackPieceRefRow"/>.
    /// Returns null when the ref doesn't resolve (caller skips the row, matching
    /// the original behavior — unresolved refs are dropped silently at save
    /// time; the seeder is the path that surfaces them).
    /// </summary>
    private readonly record struct PieceRefResolution(
        long    PieceId,
        long?   VersionId,
        long?   EndPieceId,
        long?   StartMarkerId,
        long?   EndMarkerId,
        string? DisplayLabel);

    private static PieceRefResolution? ResolvePieceRef(
        TrackPieceRef pieceRef,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel)
    {
        var resolved = resolver.TryResolve(pieceRef);
        if (resolved is null) return null;
        var (piece, version) = resolved.Value;
        if (!rowIdByPieceModel.TryGetValue(piece, out var pieceRowId)) return null;

        long? versionRowId = null;
        if (version is not null && rowIdByVersionModel.TryGetValue(version, out var vr))
            versionRowId = vr;

        long? endPieceRowId = null;
        if (pieceRef.EndSubpiecePath is { Count: > 0 })
        {
            var endProbe = new TrackPieceRef
            {
                Composer           = pieceRef.Composer,
                PieceTitle         = pieceRef.PieceTitle,
                VersionDescription = pieceRef.VersionDescription,
                SubpiecePath       = pieceRef.EndSubpiecePath,
            };
            var endResolved = resolver.TryResolve(endProbe);
            if (endResolved is not null &&
                rowIdByPieceModel.TryGetValue(endResolved.Value.Piece, out var endRowId))
                endPieceRowId = endRowId;
        }

        return new PieceRefResolution(
            pieceRowId,
            versionRowId,
            endPieceRowId,
            pieceRef.StartMarker is { Id: > 0 } sm ? sm.Id : null,
            pieceRef.EndMarker   is { Id: > 0 } em ? em.Id : null,
            pieceRef.DisplayLabel);
    }

    /// <summary>
    /// Builds the same composite identity key shape as
    /// <see cref="CanonAlbum.IdentityKey"/>, but from raw row fields so the
    /// save-side <c>existingByKey</c> dictionary keys exactly match what the
    /// model produces. Returns null only when every component is empty.
    /// </summary>
    private static string? BuildAlbumIdentityKey(string? label, string? catalogueNumber, string? title, string? subtitle)
    {
        var l  = (label           ?? "").Trim();
        var cn = (catalogueNumber ?? "").Trim();
        var t  = (title           ?? "").Trim();
        var st = (subtitle        ?? "").Trim();
        if (l.Length == 0 && cn.Length == 0 && t.Length == 0 && st.Length == 0) return null;
        return $"{l}|{cn}|{t}|{st}";
    }

    private static (AlbumRow Row, Dictionary<int, AlbumSessionRow> SessionMap) MapAlbumModelToRow(
        CanonAlbum album,
        PieceReferenceIndex resolver,
        Dictionary<CanonPiece, long> rowIdByPieceModel,
        Dictionary<CanonPieceVersion, long> rowIdByVersionModel)
    {
        var row = new AlbumRow
        {
            Title           = album.Title,
            Subtitle        = album.Subtitle,
            Label           = album.Label,
            CatalogueNumber = album.CatalogueNumber,
            Barcode         = album.Barcode,
            SparsCode       = album.SparsCode,
            IsStereo        = album.IsStereo,
            Notes           = album.Notes,
            ArchiveFolder   = album.ArchiveFolder,
            IsProvisional   = album.IsProvisional,
        };

        var volumeRowByNumber = new Dictionary<int, AlbumVolumeRow>();
        if (album.Volumes is { Count: > 0 })
        {
            foreach (var v in album.Volumes)
            {
                var vr = new AlbumVolumeRow
                {
                    Number   = v.Number,
                    Title    = v.Title,
                    Subtitle = v.Subtitle,
                };
                row.Volumes.Add(vr);
                volumeRowByNumber[v.Number] = vr;
            }
        }

        var sessionRowByIndex = new Dictionary<int, AlbumSessionRow>();
        // H21 slice 1: parallel by-stable-Id lookup so tracks carrying SessionId
        // can resolve against the corresponding fresh AlbumSessionRow when this
        // album is inserted brand-new (e.g. iTunes import, or first save of a
        // hand-built album).
        var sessionRowById = new Dictionary<long, AlbumSessionRow>();
        if (album.Sessions is { Count: > 0 })
        {
            for (int i = 0; i < album.Sessions.Count; i++)
            {
                var s = album.Sessions[i];
                var sr = new AlbumSessionRow
                {
                    Position      = i,
                    Dates         = s.Dates,
                    Venue         = s.Venue,
                    City          = s.City,
                    Country       = s.Country,
                    EngineersJson = SerializeStringList(s.Engineers),
                    ProducersJson = SerializeStringList(s.Producers),
                };
                row.Sessions.Add(sr);
                sessionRowByIndex[i] = sr;
                if (s.Id != 0) sessionRowById[s.Id] = sr;
            }
        }

        if (album.Performers is { Count: > 0 })
            for (int i = 0; i < album.Performers.Count; i++)
                row.Performers.Add(MapPerformerModelToRow(album.Performers[i], i));

        foreach (var disc in album.Discs)
        {
            var dr = new AlbumDiscRow
            {
                DiscNumber = disc.DiscNumber,
                Title      = disc.Title,
                FolderName = disc.FolderName,
            };
            if (disc.VolumeNumber is int vn && volumeRowByNumber.TryGetValue(vn, out var volRow))
                dr.Volume = volRow;

            foreach (var track in disc.Tracks)
            {
                var tr = new AlbumTrackRow
                {
                    TrackNumber   = track.TrackNumber,
                    Duration      = track.Duration,
                    Description   = track.Description,
                    SparsCode     = track.SparsCode,
                    IsStereo      = track.IsStereo,
                    IsProvisional = track.IsProvisional,
                    FlacPath      = track.FlacPath,
                    Mp3Path       = track.Mp3Path,
                };
                // H21 slice 1: prefer stable SessionId, fall back to legacy
                // positional SessionIndex.
                if (track.SessionId is long sid && sessionRowById.TryGetValue(sid, out var sessRowById))
                    tr.Session = sessRowById;
                else if (track.SessionIndex is int si && sessionRowByIndex.TryGetValue(si, out var sessRow))
                    tr.Session = sessRow;

                if (track.Performers is { Count: > 0 })
                    for (int i = 0; i < track.Performers.Count; i++)
                    {
                        var pRow = MapPerformerModelToRow(track.Performers[i], i);
                        pRow.Track = tr;
                        row.Performers.Add(pRow);
                    }

                if (track.PieceRefs is { Count: > 0 })
                {
                    int p = 0;
                    foreach (var pieceRef in track.PieceRefs)
                    {
                        var resolved = resolver.TryResolve(pieceRef);
                        if (resolved is null) continue;
                        var (piece, version) = resolved.Value;
                        if (!rowIdByPieceModel.TryGetValue(piece, out var pieceRowId)) continue;
                        long? versionRowId = null;
                        if (version is not null && rowIdByVersionModel.TryGetValue(version, out var vr))
                            versionRowId = vr;

                        // Resolve the end-piece for range refs (same composer / piece title,
                        // but use the EndSubpiecePath to walk into a different leaf). We
                        // reuse the resolver via a synthetic ref so loose-match rules apply.
                        long? endPieceRowId = null;
                        if (pieceRef.EndSubpiecePath is { Count: > 0 })
                        {
                            var endProbe = new TrackPieceRef
                            {
                                Composer           = pieceRef.Composer,
                                PieceTitle         = pieceRef.PieceTitle,
                                VersionDescription = pieceRef.VersionDescription,
                                SubpiecePath       = pieceRef.EndSubpiecePath,
                            };
                            var endResolved = resolver.TryResolve(endProbe);
                            if (endResolved is not null &&
                                rowIdByPieceModel.TryGetValue(endResolved.Value.Piece, out var endRowId))
                            {
                                endPieceRowId = endRowId;
                            }
                        }

                        tr.PieceRefs.Add(new AlbumTrackPieceRefRow
                        {
                            Position      = p++,
                            PieceId       = pieceRowId,
                            VersionId     = versionRowId,
                            EndPieceId    = endPieceRowId,
                            // Marker FKs only meaningful when the marker exists in the DB
                            // (Id != 0). Fallback fields on the MarkerReference are kept
                            // on the model but not persisted; they only matter for JSON
                            // import where the id is unknown.
                            StartMarkerId = pieceRef.StartMarker is { Id: > 0 } sm ? sm.Id : null,
                            EndMarkerId   = pieceRef.EndMarker   is { Id: > 0 } em ? em.Id : null,
                            DisplayLabel  = pieceRef.DisplayLabel,
                        });
                    }
                }

                dr.Tracks.Add(tr);
            }

            row.Discs.Add(dr);
        }

        return (row, sessionRowByIndex);
    }

    private static AlbumPerformerRow MapPerformerModelToRow(AlbumPerformer p, int position) => new()
    {
        Position    = position,
        DisplayName = p.Name,
        Role        = p.Role,
        Instrument  = p.Instrument,
    };

    private static string? SerializeStringList(List<string>? list) =>
        list is { Count: > 0 } ? JsonSerializer.Serialize(list, WriteOptions) : null;
}
