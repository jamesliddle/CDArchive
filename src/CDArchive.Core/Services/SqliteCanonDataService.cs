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

    // Schema migrations subsystem extracted to SqliteCanonDataService.Migrations.cs
    // (H1 slice 6 — the final slice of the H1 god-class split arc).
    // ApplySchemaUpgradesAsync (called from EnsureInitializedAsync above) +
    // EnsureColumnAsync + EnsureColumnNullableAsync + DropOrphanRecreateTablesAsync
    // + the RecreateXxx recipe family + WithForeignKeysOffAsync + ExecAsync all
    // live in the partial there.

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
