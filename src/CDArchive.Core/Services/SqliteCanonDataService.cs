using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;

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
public class SqliteCanonDataService : ICanonDataService
{
    private readonly IDbContextFactory<CanonDbContext> _dbFactory;
    private readonly CanonDataService _jsonService;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private sealed class IdHandle { public long Id; }

    private readonly ConditionalWeakTable<CanonComposer, IdHandle>     _composerIds = new();
    private readonly ConditionalWeakTable<CanonPiece, IdHandle>        _pieceIds    = new();
    private readonly ConditionalWeakTable<CanonPieceVersion, IdHandle> _versionIds  = new();
    private readonly ConditionalWeakTable<CanonAlbum, IdHandle>        _albumIds    = new();

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

    public SqliteCanonDataService(IDbContextFactory<CanonDbContext> dbFactory, CanonDataService jsonService)
    {
        _dbFactory   = dbFactory;
        _jsonService = jsonService;
    }

    public string ComposersFilePath => _jsonService.ComposersFilePath;
    public string PiecesFilePath    => _jsonService.PiecesFilePath;
    public string AlbumsFilePath    => _jsonService.AlbumsFilePath;
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

    // ─────────────────────────────────────────────────────────────────────────
    // Composers
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<CanonComposer>> LoadComposersAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rows = await db.Composers
            .AsNoTracking()
            .Include(c => c.Aliases)
            .Include(c => c.CatalogPrefixes)
            .OrderBy(c => c.SortName)
            .ToListAsync()
            .ConfigureAwait(false);

        var result = new List<CanonComposer>(rows.Count);
        foreach (var r in rows)
        {
            var m = MapComposerRowToModel(r);
            _composerIds.AddOrUpdate(m, new IdHandle { Id = r.Id });
            result.Add(m);
        }
        return result;
    }

    private static CanonComposer MapComposerRowToModel(ComposerRow r) => new()
    {
        Name           = r.Name,
        SortName       = r.SortName,
        BirthDate      = r.BirthDate,
        BirthPlace     = r.BirthPlace,
        BirthState     = r.BirthState,
        BirthCountry   = r.BirthCountry,
        DeathDate      = r.DeathDate,
        DeathPlace     = r.DeathPlace,
        DeathState     = r.DeathState,
        DeathCountry   = r.DeathCountry,
        Notes          = r.Notes,
        IsProvisional  = r.IsProvisional,
        Aliases         = r.Aliases.Count == 0
                          ? null
                          : r.Aliases.OrderBy(a => a.Position).Select(a => a.Alias).ToList(),
        CatalogPrefixes = r.CatalogPrefixes.Count == 0
                          ? null
                          : r.CatalogPrefixes.OrderBy(p => p.Position).Select(p => p.Prefix).ToList(),
    };

    public async Task SaveComposersAsync(List<CanonComposer> composers)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var existing = await db.Composers
            .Include(c => c.Aliases)
            .Include(c => c.CatalogPrefixes)
            .ToListAsync()
            .ConfigureAwait(false);
        var byId   = existing.ToDictionary(r => r.Id);
        var byName = new Dictionary<string, ComposerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in existing)
            byName[r.Name] = r;

        var matched = new List<(CanonComposer model, ComposerRow row)>();
        foreach (var m in composers)
        {
            ComposerRow? row = null;
            if (_composerIds.TryGetValue(m, out var handle) && byId.TryGetValue(handle.Id, out var hr))
                row = hr;
            else if (byName.TryGetValue(m.Name, out var nr))
                row = nr;

            if (row is null)
            {
                row = new ComposerRow();
                db.Composers.Add(row);
            }

            row.Name         = m.Name;
            row.SortName     = !string.IsNullOrEmpty(m.SortName) ? m.SortName : m.Name;
            row.BirthDate    = m.BirthDate;
            row.BirthPlace   = m.BirthPlace;
            row.BirthState   = m.BirthState;
            row.BirthCountry = m.BirthCountry;
            // BirthNotes has no model counterpart — preserved on existing rows, null on new.
            row.DeathDate    = m.DeathDate;
            row.DeathPlace   = m.DeathPlace;
            row.DeathState    = m.DeathState;
            row.DeathCountry  = m.DeathCountry;
            row.Notes         = m.Notes;
            row.IsProvisional = m.IsProvisional;

            row.Aliases.Clear();
            if (m.Aliases is { Count: > 0 })
                for (int i = 0; i < m.Aliases.Count; i++)
                    row.Aliases.Add(new ComposerAliasRow { Position = i, Alias = m.Aliases[i] });

            row.CatalogPrefixes.Clear();
            if (m.CatalogPrefixes is { Count: > 0 })
                for (int i = 0; i < m.CatalogPrefixes.Count; i++)
                    row.CatalogPrefixes.Add(
                        new ComposerCatalogPrefixRow { Position = i, Prefix = m.CatalogPrefixes[i] });

            matched.Add((m, row));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // Delete composers that the input no longer references. Composer rows
        // carry an OnDelete: Restrict FK from pieces.composer_id, so a composer
        // that still owns pieces will fail the SaveChanges below — surfaced as
        // an InvalidOperationException with a clear message. Callers (Reject
        // Composer in the UI) are expected to clear the composer's pieces first.
        // Done in a separate SaveChanges so the upserts above stay committed
        // even if the deletes fail.
        var matchedRowIds = new HashSet<long>(matched.Select(p => p.row.Id));
        var toDelete = existing.Where(r => !matchedRowIds.Contains(r.Id)).ToList();
        if (toDelete.Count > 0)
        {
            foreach (var r in toDelete) db.Composers.Remove(r);
            try
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException ex)
            {
                foreach (var r in toDelete)
                    db.Entry(r).State = EntityState.Unchanged;
                var names = string.Join(", ", toDelete.Select(r => $"'{r.Name}'").Take(3));
                throw new InvalidOperationException(
                    $"Cannot delete {toDelete.Count} composer(s) ({names}…) — one or more " +
                    $"still owns pieces in the canon. Remove their pieces first.", ex);
            }
        }

        foreach (var (model, row) in matched)
        {
            if (_composerIds.TryGetValue(model, out var h)) h.Id = row.Id;
            else _composerIds.AddOrUpdate(model, new IdHandle { Id = row.Id });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pick lists
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<CanonPickLists> LoadPickListsAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var rows = await db.PickListValues
            .AsNoTracking()
            .OrderBy(v => v.ListName).ThenBy(v => v.Position)
            .ToListAsync()
            .ConfigureAwait(false);

        var pl = new CanonPickLists();
        foreach (var r in rows)
        {
            switch (r.ListName)
            {
                case "forms":            if (r.Value is not null) pl.Forms.Add(r.Value);            break;
                case "categories":       if (r.Value is not null) pl.Categories.Add(r.Value);       break;
                case "catalog_prefixes": if (r.Value is not null) pl.CatalogPrefixes.Add(r.Value);  break;
                case "key_tonalities":   if (r.Value is not null) pl.KeyTonalities.Add(r.Value);    break;
                case "voice_types":      if (r.Value is not null) pl.VoiceTypes.Add(r.Value);       break;
                case "instruments":      if (r.Value is not null) pl.Instruments.Add(r.Value);      break;
                case "creative_roles":   if (r.Value is not null) pl.CreativeRoles.Add(r.Value);    break;
                case "performer_roles":  if (r.Value is not null) pl.PerformerRoles.Add(r.Value);   break;
                case "labels":           if (r.Value is not null) pl.Labels.Add(r.Value);           break;
                case "ensembles":
                    if (!string.IsNullOrEmpty(r.ValueJson))
                    {
                        var def = JsonSerializer.Deserialize<EnsembleDefinition>(r.ValueJson, ReadOptions);
                        if (def is not null)
                        {
                            pl.Ensembles ??= new List<EnsembleDefinition>();
                            pl.Ensembles.Add(def);
                        }
                    }
                    break;
            }
        }
        return pl;
    }

    public async Task SavePickListsAsync(CanonPickLists pickLists)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Sort each list before saving (matches the JSON service's behaviour).
        pickLists.Forms.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.Categories.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.CatalogPrefixes.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.KeyTonalities.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.PerformerRoles.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.Labels.Sort(StringComparer.OrdinalIgnoreCase);

        // Pick-list values are small (~250 rows total) and not referenced by FK,
        // so the simplest correct save is delete-all-and-reinsert.
        await db.PickListValues.ExecuteDeleteAsync().ConfigureAwait(false);

        AddStringList(db, "forms",            pickLists.Forms);
        AddStringList(db, "categories",       pickLists.Categories);
        AddStringList(db, "catalog_prefixes", pickLists.CatalogPrefixes);
        AddStringList(db, "key_tonalities",   pickLists.KeyTonalities);
        AddStringList(db, "voice_types",      pickLists.VoiceTypes);
        AddStringList(db, "instruments",      pickLists.Instruments);
        AddStringList(db, "creative_roles",   pickLists.CreativeRoles);
        AddStringList(db, "performer_roles",  pickLists.PerformerRoles);
        AddStringList(db, "labels",           pickLists.Labels);

        if (pickLists.Ensembles is { Count: > 0 })
        {
            for (int i = 0; i < pickLists.Ensembles.Count; i++)
            {
                db.PickListValues.Add(new PickListValueRow
                {
                    ListName  = "ensembles",
                    Position  = i,
                    Value     = pickLists.Ensembles[i].Name,
                    ValueJson = JsonSerializer.Serialize(pickLists.Ensembles[i], WriteOptions),
                });
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void AddStringList(CanonDbContext db, string listName, IList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
            db.PickListValues.Add(new PickListValueRow
            {
                ListName = listName,
                Position = i,
                Value    = values[i],
            });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Pieces
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<CanonPiece>> LoadPiecesAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var (pieces, _, _, _, _) = await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);
        return pieces;
    }

    /// <summary>
    /// Loads every piece row, version row, and child collection from the DB and
    /// reconstructs the full <see cref="CanonPiece"/> tree along with maps from
    /// row id back to model and to row, used for album-ref resolution and
    /// identity tracking. Single trip per logical operation.
    /// </summary>
    private async Task<(
        List<CanonPiece> topPieces,
        Dictionary<long, CanonPiece> pieceModelByRowId,
        Dictionary<long, CanonPieceVersion> versionModelByRowId,
        Dictionary<long, PieceRow> pieceRowById,
        Dictionary<long, PieceVersionRow> versionRowById)> LoadAllPiecesInternalAsync(CanonDbContext db)
    {
        var composerRows = await db.Composers.AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.SortName })
            .ToListAsync().ConfigureAwait(false);
        var composerNameById = composerRows.ToDictionary(c => c.Id, c => c.Name);
        var composerSortByName = composerRows
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SortName, StringComparer.OrdinalIgnoreCase);

        var pieceRows   = await db.Pieces.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var versionRows = await db.PieceVersions.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var catEntries  = await db.PieceCatalogEntries.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var markers     = await db.PieceMarkers.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var credits     = await db.PieceComposerCredits.AsNoTracking().ToListAsync().ConfigureAwait(false);
        var variants    = await db.PieceVariants.AsNoTracking().ToListAsync().ConfigureAwait(false);

        // Group child rows by their owner id for O(1) lookup.
        var entriesByPieceId   = catEntries.Where(e => e.PieceId.HasValue)
            .GroupBy(e => e.PieceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Position).ToList());
        var entriesByVersionId = catEntries.Where(e => e.VersionId.HasValue)
            .GroupBy(e => e.VersionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Position).ToList());

        var markersByPieceId   = markers.Where(m => m.PieceId.HasValue)
            .GroupBy(m => m.PieceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Position).ToList());
        var markersByVersionId = markers.Where(m => m.VersionId.HasValue)
            .GroupBy(m => m.VersionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Position).ToList());
        var subMarkersByParent = markers.Where(m => m.ParentMarkerId.HasValue)
            .GroupBy(m => m.ParentMarkerId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Position).ToList());

        var creditsByPieceId   = credits.Where(c => c.PieceId.HasValue)
            .GroupBy(c => c.PieceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ToList());
        var creditsByVersionId = credits.Where(c => c.VersionId.HasValue)
            .GroupBy(c => c.VersionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Position).ToList());

        var variantsByPieceId  = variants.Where(v => v.PieceId.HasValue)
            .GroupBy(v => v.PieceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Position).ToList());
        var variantsByVersionId = variants.Where(v => v.VersionId.HasValue)
            .GroupBy(v => v.VersionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Position).ToList());

        var subpiecesByParentPieceId = pieceRows.Where(p => p.ParentPieceId.HasValue)
            .GroupBy(p => p.ParentPieceId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Position).ToList());
        var subpiecesByParentVersionId = pieceRows.Where(p => p.ParentVersionId.HasValue)
            .GroupBy(p => p.ParentVersionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Position).ToList());
        var versionsByPieceId = versionRows
            .GroupBy(v => v.PieceId)
            .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Position).ToList());

        var pieceRowById   = pieceRows.ToDictionary(p => p.Id);
        var versionRowById = versionRows.ToDictionary(v => v.Id);

        // First pass: create the model objects without their child collections.
        var pieceModelByRowId = new Dictionary<long, CanonPiece>(pieceRows.Count);
        foreach (var r in pieceRows)
            pieceModelByRowId[r.Id] = MapPieceRowToModelShallow(r, composerNameById);

        var versionModelByRowId = new Dictionary<long, CanonPieceVersion>(versionRows.Count);
        foreach (var v in versionRows)
            versionModelByRowId[v.Id] = MapVersionRowToModelShallow(v);

        // Second pass: attach inner collections + subpiece / version trees.
        foreach (var r in pieceRows)
        {
            var m = pieceModelByRowId[r.Id];
            if (entriesByPieceId.TryGetValue(r.Id, out var ce))
                m.CatalogInfo = ce.Select(MapCatalogEntry).ToList();
            if (markersByPieceId.TryGetValue(r.Id, out var mk))
                m.Markers = mk.Select(x => MapMarker(x, subMarkersByParent)).ToList();
            if (creditsByPieceId.TryGetValue(r.Id, out var cs))
                m.Composers = cs.Select(MapCredit).ToList();
            if (variantsByPieceId.TryGetValue(r.Id, out var vs))
                m.Variants = vs.Select(MapVariant).ToList();

            if (subpiecesByParentPieceId.TryGetValue(r.Id, out var subs))
                m.Subpieces = subs.Select(s => pieceModelByRowId[s.Id]).ToList();
            if (versionsByPieceId.TryGetValue(r.Id, out var vers))
                m.Versions = vers.Select(v => versionModelByRowId[v.Id]).ToList();
        }
        foreach (var v in versionRows)
        {
            var m = versionModelByRowId[v.Id];
            if (entriesByVersionId.TryGetValue(v.Id, out var ce))
                m.CatalogInfo = ce.Select(MapCatalogEntry).ToList();
            if (markersByVersionId.TryGetValue(v.Id, out var mk))
                m.Markers = mk.Select(x => MapMarker(x, subMarkersByParent)).ToList();
            if (creditsByVersionId.TryGetValue(v.Id, out var cs))
                m.Composers = cs.Select(MapCredit).ToList();
            if (variantsByVersionId.TryGetValue(v.Id, out var vs))
                m.Variants = vs.Select(MapVariant).ToList();

            if (subpiecesByParentVersionId.TryGetValue(v.Id, out var subs))
                m.Subpieces = subs.Select(s => pieceModelByRowId[s.Id]).ToList();
        }

        // Top-level pieces ordered by composer SortName, then position.
        var topRows = pieceRows
            .Where(p => !p.ParentPieceId.HasValue && !p.ParentVersionId.HasValue)
            .OrderBy(p => composerSortByName.TryGetValue(
                              composerNameById.TryGetValue(p.ComposerId, out var nm) ? nm : "",
                              out var sn) ? sn : nm ?? "",
                     StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Position)
            .ToList();

        var topPieces = topRows.Select(r => pieceModelByRowId[r.Id]).ToList();

        // Drop redundant subpiece composers — a movement that inherits its
        // parent's composer keeps Composer == null in memory, matching the JSON
        // convention. Collaborative works (L'éventail de Jeanne et al.) preserve
        // per-movement composers because they differ from the parent's sentinel.
        foreach (var top in topPieces)
            NormalizeInheritedComposers(top, top.Composer);

        // Update CWT identity tracking for both pieces and versions.
        foreach (var (id, m) in pieceModelByRowId)
            _pieceIds.AddOrUpdate(m, new IdHandle { Id = id });
        foreach (var (id, m) in versionModelByRowId)
            _versionIds.AddOrUpdate(m, new IdHandle { Id = id });

        return (topPieces, pieceModelByRowId, versionModelByRowId, pieceRowById, versionRowById);
    }

    /// <summary>
    /// Walks a piece's subtree and clears <see cref="CanonPiece.Composer"/> on
    /// any subpiece whose composer matches the parent's effective composer,
    /// matching the JSON-serialization convention that subpieces inherit by
    /// default. Subpieces with a different composer (e.g. each movement of
    /// <em>L'éventail de Jeanne</em>) keep their explicit composer.
    /// </summary>
    private static void NormalizeInheritedComposers(CanonPiece piece, string? inherited)
    {
        var effective = piece.Composer ?? inherited;
        if (piece.Subpieces is { Count: > 0 })
        {
            foreach (var sub in piece.Subpieces)
            {
                if (string.Equals(sub.Composer, effective, StringComparison.OrdinalIgnoreCase))
                    sub.Composer = null;
                NormalizeInheritedComposers(sub, effective);
            }
        }
        if (piece.Versions is { Count: > 0 })
        {
            foreach (var ver in piece.Versions)
            {
                if (ver.Subpieces is { Count: > 0 })
                {
                    foreach (var sub in ver.Subpieces)
                    {
                        if (string.Equals(sub.Composer, effective, StringComparison.OrdinalIgnoreCase))
                            sub.Composer = null;
                        NormalizeInheritedComposers(sub, effective);
                    }
                }
            }
        }
    }

    private static CanonPiece MapPieceRowToModelShallow(PieceRow r, Dictionary<long, string> composerNameById)
    {
        composerNameById.TryGetValue(r.ComposerId, out var composerName);
        return new CanonPiece
        {
            // Carry the resolved composer for every piece, including subpieces.
            // Most subpieces share their parent's composer (a Beethoven sonata's
            // movements all map to Beethoven), but collaborative works such as
            // L'éventail de Jeanne have per-movement composers that must round
            // trip. The JSON writer in CanonDataService still drops Composer
            // when it equals the parent's, keeping the on-disk JSON clean.
            Composer                = composerName,
            Title                   = r.Title,
            TitleEnglish            = r.TitleEnglish,
            Subtitle                = r.Subtitle,
            Nickname                = r.Nickname,
            Form                    = r.Form,
            Number                  = r.Number,
            MusicNumber             = r.MusicNumber,
            KeyTonality             = r.KeyTonality,
            KeyMode                 = r.KeyMode,
            PublicationYear         = r.PublicationYear,
            InstrumentationCategory = r.InstrumentationCategory,
            NumberedSubpieces       = r.NumberedSubpieces,
            SubpiecesStart          = r.SubpiecesStart,
            Notes                   = r.Notes,
            IsProvisional           = r.IsProvisional,

            Instrumentation  = ParseJsonElement(r.InstrumentationJson),
            CompositionYears = ParseJsonElement(r.CompositionYearsJson),
            TextAuthor       = ParseJsonElement(r.TextAuthorJson),
            Roles            = ParseJsonElement(r.RolesJson),
            Arrangements     = ParseJsonElement(r.ArrangementsJson),
            Cadenza          = ParseJsonElement(r.CadenzaJson),
            TitleNumber      = ParseJsonElement(r.TitleNumberJson),
        };
    }

    private static CanonPieceVersion MapVersionRowToModelShallow(PieceVersionRow r) => new()
    {
        Description             = r.Description,
        Title                   = r.Title,
        TitleEnglish            = r.TitleEnglish,
        Subtitle                = r.Subtitle,
        Nickname                = r.Nickname,
        Form                    = r.Form,
        Number                  = r.Number,
        MusicNumber             = r.MusicNumber,
        KeyTonality             = r.KeyTonality,
        KeyMode                 = r.KeyMode,
        PublicationYear         = r.PublicationYear,
        InstrumentationCategory = r.InstrumentationCategory,
        NumberedSubpieces       = r.NumberedSubpieces,
        SubpiecesStart          = r.SubpiecesStart,
        Notes                   = r.Notes,

        Instrumentation       = ParseJsonElement(r.InstrumentationJson),
        CompositionYears      = ParseJsonElement(r.CompositionYearsJson),
        TextAuthor            = ParseJsonElement(r.TextAuthorJson),
        Roles                 = ParseJsonElement(r.RolesJson),
        ContributingComposers = ParseJsonElement(r.ContributingComposersJson),
    };

    private static CatalogInfo MapCatalogEntry(PieceCatalogEntryRow r) => new()
    {
        Catalog          = r.Catalog,
        CatalogNumber    = r.CatalogNumber,
        CatalogSubnumber = r.CatalogSubnumber,
    };

    /// <summary>
    /// Reconstructs a <see cref="MusicalMarker"/> from a row, recursing through
    /// nested sub-markers. The <see cref="MusicalMarker.Id"/> carries the row's
    /// stable id back to the model so track refs that point at this marker
    /// resolve correctly after the load.
    /// </summary>
    private static MusicalMarker MapMarker(
        PieceMarkerRow r,
        Dictionary<long, List<PieceMarkerRow>> subMarkersByParent)
    {
        var m = new MusicalMarker
        {
            Id          = r.Id,
            Kind        = r.Kind,
            Value       = r.Value,
            BarNumber   = r.BarNumber,
            Number      = r.Number,
            Description = r.Description,
        };
        if (subMarkersByParent.TryGetValue(r.Id, out var subs))
            m.SubMarkers = subs.Select(s => MapMarker(s, subMarkersByParent)).ToList();
        return m;
    }

    private static ComposerCredit MapCredit(PieceComposerCreditRow r) => new()
    {
        Name = r.Name,
        Role = r.Role,
    };

    private static VariantInfo MapVariant(PieceVariantRow r) => new()
    {
        Description     = r.Description,
        LongDescription = r.LongDescription,
    };

    private static JsonElement? ParseJsonElement(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string? RawJson(JsonElement? element) =>
        element.HasValue ? element.Value.GetRawText() : null;

    public async Task SavePiecesAsync(List<CanonPiece> pieces)
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Resolve composer name → id for every input piece. Composers are
        // expected to exist already (saved separately). Also pull each composer's
        // ordered CatalogPrefixes preference so we can normalize CatalogInfo
        // ordering before persisting (matches the load-time normalization done
        // in CanonViewModel and keeps the canonical sort surviving a round trip).
        var composerRows = await db.Composers
            .Include(c => c.CatalogPrefixes)
            .ToListAsync()
            .ConfigureAwait(false);
        var composerIdByName = composerRows.ToDictionary(
            c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);
        var prefsByComposer = composerRows
            .Where(c => c.CatalogPrefixes.Count > 0)
            .ToDictionary(
                c => c.Name,
                c => (IReadOnlyList<string>)c.CatalogPrefixes
                    .OrderBy(p => p.Position)
                    .Select(p => p.Prefix)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        // Apply each composer's catalog-prefix preference to their pieces so the
        // normalized order propagates to both the SQLite rows below and the JSON
        // write-through at the end. Mutates the input list in-place — that's
        // intentional: callers expect SortCatalogInfoByPreference's idempotent
        // partitioning, and it keeps in-memory models aligned with disk state.
        if (prefsByComposer.Count > 0)
        {
            foreach (var piece in pieces)
            {
                if (piece.Composer is { } name &&
                    prefsByComposer.TryGetValue(name, out var prefs))
                {
                    piece.SortCatalogInfoByPreference(prefs);
                }
            }
        }

        // Load all existing pieces with the full hierarchy and inner collections.
        var existingTopRows = await db.Pieces
            .Where(p => !p.ParentPieceId.HasValue && !p.ParentVersionId.HasValue)
            .Include(p => p.Subpieces)
            .Include(p => p.Versions)
            .Include(p => p.CatalogEntries)
            .Include(p => p.Markers)
            .Include(p => p.ComposerCredits)
            .Include(p => p.Variants)
            .ToListAsync()
            .ConfigureAwait(false);

        // Recursively eager-load subpiece and version subtrees. EF can't recurse
        // an Include automatically, so we pull every PieceRow / PieceVersionRow
        // into the change tracker by querying them and let nav fix-up do the rest.
        await db.Pieces.LoadAsync().ConfigureAwait(false);
        await db.PieceVersions.LoadAsync().ConfigureAwait(false);
        await db.PieceCatalogEntries.LoadAsync().ConfigureAwait(false);
        await db.PieceMarkers.LoadAsync().ConfigureAwait(false);
        await db.PieceComposerCredits.LoadAsync().ConfigureAwait(false);
        await db.PieceVariants.LoadAsync().ConfigureAwait(false);

        var existingByComposerTitle = new Dictionary<(long composerId, string title), PieceRow>();
        foreach (var r in existingTopRows)
        {
            var key = (r.ComposerId, (r.Title ?? "").Trim().ToLowerInvariant());
            existingByComposerTitle.TryAdd(key, r);
        }
        var existingById = existingTopRows.ToDictionary(r => r.Id);

        var matched = new List<(CanonPiece model, PieceRow row)>();
        var matchedVersions = new List<(CanonPieceVersion model, PieceVersionRow row)>();

        for (int i = 0; i < pieces.Count; i++)
        {
            var input = pieces[i];

            if (string.IsNullOrWhiteSpace(input.Composer) ||
                !composerIdByName.TryGetValue(input.Composer, out var composerId))
            {
                // Skip pieces whose composer doesn't exist in the DB. Caller
                // should have saved composers first.
                continue;
            }

            PieceRow? row = null;
            if (_pieceIds.TryGetValue(input, out var handle) && existingById.TryGetValue(handle.Id, out var hr))
                row = hr;
            else
            {
                var key = (composerId, (input.Title ?? "").Trim().ToLowerInvariant());
                if (existingByComposerTitle.TryGetValue(key, out var er)) row = er;
            }

            if (row is null)
            {
                row = new PieceRow { ComposerId = composerId };
                db.Pieces.Add(row);
            }
            else
            {
                row.ComposerId = composerId;
            }

            UpsertPieceTree(db, input, row, composerId, position: i, matched, matchedVersions);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // ── Delete top-level rows that the input no longer references. ──
        // Without this pass, removing a piece from CanonViewModel.Pieces and saving
        // would only persist the remaining upserts; the dropped piece's row would
        // stay in the DB and reappear on next load.
        //
        // Run in a separate SaveChanges so an FK-restrict failure on one delete
        // (album refs still point at the piece) doesn't roll back the upserts
        // from the loop above. The album-refs FK is OnDelete: Restrict, so the
        // failure surfaces here as an exception that propagates to the caller.
        var matchedTopRowIds = new HashSet<long>(
            matched.Where(p => p.row.ParentPieceId is null && p.row.ParentVersionId is null)
                   .Select(p => p.row.Id));

        var toDelete = existingTopRows
            .Where(r => !matchedTopRowIds.Contains(r.Id))
            .ToList();

        if (toDelete.Count > 0)
        {
            foreach (var r in toDelete) db.Pieces.Remove(r);
            try
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (DbUpdateException ex)
            {
                // Re-attach the rows so subsequent saves don't keep re-trying the delete.
                foreach (var r in toDelete)
                    db.Entry(r).State = EntityState.Unchanged;
                var titles = string.Join(", ",
                    toDelete.Select(r => $"'{r.Title}'").Where(s => s.Length > 2).Take(3));
                throw new InvalidOperationException(
                    $"Cannot delete {toDelete.Count} piece(s) ({titles}…) — they are still " +
                    $"referenced by one or more album track refs. Remove those album references first.",
                    ex);
            }
            // CWT entries (_pieceIds) for the deleted CanonPieces clean themselves
            // up when the in-memory models become unreachable (ConditionalWeakTable
            // is GC-aware) — no explicit removal required here.
        }

        // Update CWT for every piece / version we touched.
        foreach (var (m, r) in matched)
        {
            if (_pieceIds.TryGetValue(m, out var h)) h.Id = r.Id;
            else _pieceIds.AddOrUpdate(m, new IdHandle { Id = r.Id });
        }
        foreach (var (m, r) in matchedVersions)
        {
            if (_versionIds.TryGetValue(m, out var h)) h.Id = r.Id;
            else _versionIds.AddOrUpdate(m, new IdHandle { Id = r.Id });
        }
    }

    /// <summary>
    /// Upserts a <see cref="PieceRow"/> in place: copies scalar fields, replaces
    /// the inner collections wholesale, and recursively reconciles subpieces /
    /// versions by object identity (CWT) and a (composer, title)-or-position
    /// composite key. Children that exist in the row but not in the input are
    /// removed; the cascade FK clears their inner rows. Album refs to a removed
    /// piece block the save (<c>OnDelete: Restrict</c>).
    /// </summary>
    private void UpsertPieceTree(
        CanonDbContext db,
        CanonPiece input,
        PieceRow row,
        long composerId,
        int position,
        List<(CanonPiece, PieceRow)> matched,
        List<(CanonPieceVersion, PieceVersionRow)> matchedVersions)
    {
        ApplyPieceFields(row, input, composerId, position);

        ReplaceCatalogEntriesPiece(row, input.CatalogInfo, db);
        ReplaceMarkersPiece(row, input.Markers, db);
        ReplaceCreditsPiece(row, input.Composers, db);
        ReplaceVariantsPiece(row, input.Variants, db);

        // ── Subpieces ────────────────────────────────────────────────────────
        var existingSubs = row.Subpieces.ToList();
        var existingSubById     = existingSubs.ToDictionary(s => s.Id);
        var existingSubByTitle  = new Dictionary<string, PieceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in existingSubs)
            existingSubByTitle.TryAdd((s.Title ?? "").Trim(), s);
        var matchedSubIds = new HashSet<long>();

        if (input.Subpieces is { Count: > 0 })
        {
            for (int i = 0; i < input.Subpieces.Count; i++)
            {
                var sub = input.Subpieces[i];
                PieceRow? subRow = null;
                if (_pieceIds.TryGetValue(sub, out var handle) && existingSubById.TryGetValue(handle.Id, out var hr))
                    subRow = hr;
                else if (!string.IsNullOrEmpty(sub.Title) &&
                         existingSubByTitle.TryGetValue(sub.Title.Trim(), out var tr))
                    subRow = tr;
                else if (i < existingSubs.Count)
                    // Position fallback: subpieces often lack stable titles, so
                    // pair input[i] with existing[i] when nothing else matches.
                    subRow = existingSubs[i];

                // Subpieces inherit the parent's composer unless they declare
                // their own (collaborative works like L'éventail de Jeanne, where
                // each movement has a different composer). Computed here rather
                // than inside UpsertPieceTree because ApplyPieceFields would
                // otherwise overwrite the row's composer with the parent's.
                var subComposerId = ResolveSubpieceComposerId(sub.Composer, composerId, db);

                if (subRow is null || matchedSubIds.Contains(subRow.Id))
                {
                    subRow = new PieceRow { ComposerId = subComposerId, ParentPiece = row };
                    row.Subpieces.Add(subRow);
                }
                else
                {
                    matchedSubIds.Add(subRow.Id);
                }

                UpsertPieceTree(db, sub, subRow, subComposerId, i, matched, matchedVersions);
            }
        }
        foreach (var leftover in existingSubs)
        {
            if (leftover.Id != 0 && !matchedSubIds.Contains(leftover.Id))
                db.Pieces.Remove(leftover);
        }

        // ── Versions ─────────────────────────────────────────────────────────
        var existingVers = row.Versions.ToList();
        var existingVerById     = existingVers.ToDictionary(v => v.Id);
        var existingVerByDesc   = new Dictionary<string, PieceVersionRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existingVers)
            existingVerByDesc.TryAdd((v.Description ?? "").Trim(), v);
        var matchedVerIds = new HashSet<long>();

        if (input.Versions is { Count: > 0 })
        {
            for (int i = 0; i < input.Versions.Count; i++)
            {
                var ver = input.Versions[i];
                PieceVersionRow? verRow = null;
                if (_versionIds.TryGetValue(ver, out var handle) && existingVerById.TryGetValue(handle.Id, out var hr))
                    verRow = hr;
                else if (!string.IsNullOrEmpty(ver.Description) &&
                         existingVerByDesc.TryGetValue(ver.Description.Trim(), out var dr))
                    verRow = dr;
                else if (i < existingVers.Count)
                    verRow = existingVers[i];

                if (verRow is null || matchedVerIds.Contains(verRow.Id))
                {
                    verRow = new PieceVersionRow { Piece = row };
                    row.Versions.Add(verRow);
                }
                else
                {
                    matchedVerIds.Add(verRow.Id);
                }

                UpsertVersion(db, ver, verRow, composerId, i, matched, matchedVersions);
            }
        }
        foreach (var leftover in existingVers)
        {
            if (leftover.Id != 0 && !matchedVerIds.Contains(leftover.Id))
                db.PieceVersions.Remove(leftover);
        }

        matched.Add((input, row));
    }

    private void UpsertVersion(
        CanonDbContext db,
        CanonPieceVersion input,
        PieceVersionRow row,
        long composerId,
        int position,
        List<(CanonPiece, PieceRow)> matched,
        List<(CanonPieceVersion, PieceVersionRow)> matchedVersions)
    {
        ApplyVersionFields(row, input, position);

        ReplaceCatalogEntriesVersion(row, input.CatalogInfo, db);
        ReplaceMarkersVersion(row, input.Markers, db);
        ReplaceCreditsVersion(row, input.Composers, db);
        ReplaceVariantsVersion(row, input.Variants, db);

        // Version subpieces live in the pieces table with parent_version_id set.
        var existingSubs = row.Subpieces.ToList();
        var existingSubById    = existingSubs.ToDictionary(s => s.Id);
        var existingSubByTitle = new Dictionary<string, PieceRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in existingSubs)
            existingSubByTitle.TryAdd((s.Title ?? "").Trim(), s);
        var matchedSubIds = new HashSet<long>();

        if (input.Subpieces is { Count: > 0 })
        {
            for (int i = 0; i < input.Subpieces.Count; i++)
            {
                var sub = input.Subpieces[i];
                PieceRow? subRow = null;
                if (_pieceIds.TryGetValue(sub, out var handle) && existingSubById.TryGetValue(handle.Id, out var hr))
                    subRow = hr;
                else if (!string.IsNullOrEmpty(sub.Title) &&
                         existingSubByTitle.TryGetValue(sub.Title.Trim(), out var tr))
                    subRow = tr;
                else if (i < existingSubs.Count)
                    subRow = existingSubs[i];

                // Honour subpiece-level composer overrides (collaborative works).
                var subComposerId = ResolveSubpieceComposerId(sub.Composer, composerId, db);

                if (subRow is null || matchedSubIds.Contains(subRow.Id))
                {
                    subRow = new PieceRow { ComposerId = subComposerId, ParentVersion = row };
                    row.Subpieces.Add(subRow);
                }
                else
                {
                    matchedSubIds.Add(subRow.Id);
                }

                UpsertPieceTree(db, sub, subRow, subComposerId, i, matched, matchedVersions);
            }
        }
        foreach (var leftover in existingSubs)
        {
            if (leftover.Id != 0 && !matchedSubIds.Contains(leftover.Id))
                db.Pieces.Remove(leftover);
        }

        matchedVersions.Add((input, row));
    }

    private static void ApplyPieceFields(PieceRow row, CanonPiece m, long composerId, int position)
    {
        row.ComposerId              = composerId;
        row.Position                = position;
        row.Title                   = m.Title;
        row.TitleEnglish            = m.TitleEnglish;
        row.Subtitle                = m.Subtitle;
        row.Nickname                = m.Nickname;
        row.Form                    = m.Form;
        row.Number                  = m.Number;
        row.MusicNumber             = m.MusicNumber;
        row.KeyTonality             = m.KeyTonality;
        row.KeyMode                 = m.KeyMode;
        row.PublicationYear         = m.PublicationYear;
        row.InstrumentationCategory = m.InstrumentationCategory;
        row.NumberedSubpieces       = m.NumberedSubpieces;
        row.SubpiecesStart          = m.SubpiecesStart;
        row.Notes                   = m.Notes;
        row.IsProvisional           = m.IsProvisional;

        row.InstrumentationJson  = RawJson(m.Instrumentation);
        row.CompositionYearsJson = RawJson(m.CompositionYears);
        row.TextAuthorJson       = RawJson(m.TextAuthor);
        row.RolesJson            = RawJson(m.Roles);
        row.ArrangementsJson     = RawJson(m.Arrangements);
        row.CadenzaJson          = RawJson(m.Cadenza);
        row.TitleNumberJson      = RawJson(m.TitleNumber);

        row.CatalogSortPrefix = m.CatalogSortPrefix;
        row.CatalogSortNumber = m.CatalogSortNumber;
        row.CatalogSortSuffix = m.CatalogSortSuffix;
    }

    private static void ApplyVersionFields(PieceVersionRow row, CanonPieceVersion m, int position)
    {
        row.Position                = position;
        row.Description             = m.Description;
        row.Title                   = m.Title;
        row.TitleEnglish            = m.TitleEnglish;
        row.Subtitle                = m.Subtitle;
        row.Nickname                = m.Nickname;
        row.Form                    = m.Form;
        row.Number                  = m.Number;
        row.MusicNumber             = m.MusicNumber;
        row.KeyTonality             = m.KeyTonality;
        row.KeyMode                 = m.KeyMode;
        row.PublicationYear         = m.PublicationYear;
        row.InstrumentationCategory = m.InstrumentationCategory;
        row.NumberedSubpieces       = m.NumberedSubpieces;
        row.SubpiecesStart          = m.SubpiecesStart;
        row.Notes                   = m.Notes;

        row.InstrumentationJson       = RawJson(m.Instrumentation);
        row.CompositionYearsJson      = RawJson(m.CompositionYears);
        row.TextAuthorJson            = RawJson(m.TextAuthor);
        row.RolesJson                 = RawJson(m.Roles);
        row.ContributingComposersJson = RawJson(m.ContributingComposers);
    }

    // ── Replace inner collections ────────────────────────────────────────────
    // Each Replace method explicitly Removes the existing rows before clearing
    // the navigation collection. EF Core would otherwise null the FK on orphan
    // (because PieceId / VersionId are both nullable to allow either-owner
    // semantics) and our CHECK constraint —
    //   "exactly one of (piece_id, version_id) is non-null" —
    // rejects rows with both nulls. The same pattern applies to PieceTempoRow
    // (three nullable owners) and the PieceComposerCreditRow / PieceVariantRow
    // pairs which would simply leak orphaned rows on save without the explicit
    // remove.

    private static void ReplaceCatalogEntriesPiece(PieceRow row, List<CatalogInfo>? entries, CanonDbContext db)
    {
        foreach (var existing in row.CatalogEntries.ToList())
            db.PieceCatalogEntries.Remove(existing);
        row.CatalogEntries.Clear();
        if (entries is null) return;
        for (int i = 0; i < entries.Count; i++)
            row.CatalogEntries.Add(new PieceCatalogEntryRow
            {
                Position         = i,
                Catalog          = entries[i].Catalog,
                CatalogNumber    = entries[i].CatalogNumber,
                CatalogSubnumber = entries[i].CatalogSubnumber,
            });
    }

    private static void ReplaceCatalogEntriesVersion(PieceVersionRow row, List<CatalogInfo>? entries, CanonDbContext db)
    {
        foreach (var existing in row.CatalogEntries.ToList())
            db.PieceCatalogEntries.Remove(existing);
        row.CatalogEntries.Clear();
        if (entries is null) return;
        for (int i = 0; i < entries.Count; i++)
            row.CatalogEntries.Add(new PieceCatalogEntryRow
            {
                Position         = i,
                Catalog          = entries[i].Catalog,
                CatalogNumber    = entries[i].CatalogNumber,
                CatalogSubnumber = entries[i].CatalogSubnumber,
            });
    }

    // (Tempos are markers now — see Replace*Markers below. The legacy
    //  ReplaceTemposPiece / ReplaceTemposVersion / RemoveTempoTree helpers
    //  are gone.)

    // ── Markers ──────────────────────────────────────────────────────────────
    // Markers must preserve stable IDs across saves so album-track refs that
    // point at them keep resolving. The Replace* methods reconcile by id:
    // input markers with a non-zero Id update the existing row in place;
    // input markers with Id == 0 become new rows; existing rows whose id
    // doesn't appear in the input are removed.

    private static void ReplaceMarkersPiece(PieceRow row, List<MusicalMarker>? markers, CanonDbContext db)
    {
        ReconcileMarkers(row.Markers, markers, db,
            attachToPiece: piece => row.Markers.Add(piece));
    }

    private static void ReplaceMarkersVersion(PieceVersionRow row, List<MusicalMarker>? markers, CanonDbContext db)
    {
        ReconcileMarkers(row.Markers, markers, db,
            attachToPiece: piece => row.Markers.Add(piece));
    }

    /// <summary>
    /// Reconciles a list of existing <see cref="PieceMarkerRow"/>s against an
    /// incoming list of <see cref="MusicalMarker"/>s, preserving row IDs by
    /// matching on <see cref="MusicalMarker.Id"/>. Removes orphans, updates
    /// matched rows in place, and adds genuinely new rows. The
    /// <paramref name="attachToPiece"/> callback adds new top-level markers
    /// to the owning piece/version's navigation collection so EF assigns the
    /// correct FK on save.
    /// </summary>
    private static void ReconcileMarkers(
        List<PieceMarkerRow> existing,
        List<MusicalMarker>? incoming,
        CanonDbContext db,
        Action<PieceMarkerRow> attachToPiece)
    {
        var existingById = existing.ToDictionary(m => m.Id);
        var keepIds = new HashSet<long>();

        // Pass 1: walk the incoming list in order; update or create each marker.
        if (incoming is not null)
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                var src = incoming[i];
                PieceMarkerRow row;
                if (src.Id != 0 && existingById.TryGetValue(src.Id, out var matched))
                {
                    row = matched;
                    keepIds.Add(row.Id);
                }
                else
                {
                    row = new PieceMarkerRow();
                    attachToPiece(row);
                }

                row.Position    = i;
                row.Kind        = src.Kind;
                row.Value       = src.Value;
                row.BarNumber   = src.BarNumber;
                row.Number      = src.Number;
                row.Description = src.Description;

                ReconcileSubMarkers(row, src.SubMarkers, db, keepIds);
            }
        }

        // Pass 2: remove anything in the existing list not retained.
        // Have to traverse subtrees so nested rows get explicitly Remove()d
        // — the CHECK constraint refuses null-FK orphans (same trap as tempos).
        foreach (var orphan in existing.ToList())
        {
            if (keepIds.Contains(orphan.Id)) continue;
            RemoveMarkerTree(orphan, db);
            existing.Remove(orphan);
        }
    }

    private static void ReconcileSubMarkers(
        PieceMarkerRow parent,
        List<MusicalMarker>? incoming,
        CanonDbContext db,
        HashSet<long> keepIds)
    {
        var existing = parent.SubMarkers.ToList();
        var existingById = existing.ToDictionary(m => m.Id);

        if (incoming is not null)
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                var src = incoming[i];
                PieceMarkerRow row;
                if (src.Id != 0 && existingById.TryGetValue(src.Id, out var matched))
                {
                    row = matched;
                    keepIds.Add(row.Id);
                }
                else
                {
                    row = new PieceMarkerRow();
                    parent.SubMarkers.Add(row);
                }

                row.Position    = i;
                row.Kind        = src.Kind;
                row.Value       = src.Value;
                row.BarNumber   = src.BarNumber;
                row.Number      = src.Number;
                row.Description = src.Description;

                ReconcileSubMarkers(row, src.SubMarkers, db, keepIds);
            }
        }

        foreach (var orphan in existing)
        {
            if (keepIds.Contains(orphan.Id)) continue;
            RemoveMarkerTree(orphan, db);
            parent.SubMarkers.Remove(orphan);
        }
    }

    /// <summary>
    /// Removes a marker and its sub-markers from the change tracker. Same
    /// rationale as <see cref="RemoveTempoTree"/>: the multi-owner CHECK
    /// constraint rejects null-FK orphans, so explicit removes are required.
    /// </summary>
    private static void RemoveMarkerTree(PieceMarkerRow m, CanonDbContext db)
    {
        if (m.SubMarkers is { Count: > 0 })
            foreach (var sub in m.SubMarkers.ToList())
                RemoveMarkerTree(sub, db);
        db.PieceMarkers.Remove(m);
    }

    private static void ReplaceCreditsPiece(PieceRow row, List<ComposerCredit>? credits, CanonDbContext db)
    {
        foreach (var existing in row.ComposerCredits.ToList())
            db.PieceComposerCredits.Remove(existing);
        row.ComposerCredits.Clear();
        if (credits is null) return;
        for (int i = 0; i < credits.Count; i++)
            row.ComposerCredits.Add(new PieceComposerCreditRow
            {
                Position   = i,
                ComposerId = ResolveCreditComposerId(credits[i].Name, db),
                Name       = credits[i].Name,
                Role       = credits[i].Role,
            });
    }

    private static void ReplaceCreditsVersion(PieceVersionRow row, List<ComposerCredit>? credits, CanonDbContext db)
    {
        foreach (var existing in row.ComposerCredits.ToList())
            db.PieceComposerCredits.Remove(existing);
        row.ComposerCredits.Clear();
        if (credits is null) return;
        for (int i = 0; i < credits.Count; i++)
            row.ComposerCredits.Add(new PieceComposerCreditRow
            {
                Position   = i,
                ComposerId = ResolveCreditComposerId(credits[i].Name, db),
                Name       = credits[i].Name,
                Role       = credits[i].Role,
            });
    }

    private static long? ResolveCreditComposerId(string? name, CanonDbContext db)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // ChangeTracker is populated by the bulk Load() calls in SavePiecesAsync,
        // so this is an in-memory scan rather than a DB hit.
        var match = db.Composers.Local
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return match?.Id;
    }

    /// <summary>
    /// Resolves a subpiece's composer id, honouring an explicit subpiece-level
    /// <c>composer</c> field when present, falling back to the parent's composer
    /// id. Used for collaborative works (e.g. <em>L'éventail de Jeanne</em>)
    /// where the parent piece's composer is the sentinel <c>(Various)</c> and
    /// each movement carries its real composer in the subpiece-level field.
    /// </summary>
    private static long ResolveSubpieceComposerId(string? subpieceComposer, long fallbackComposerId, CanonDbContext db)
    {
        if (string.IsNullOrWhiteSpace(subpieceComposer)) return fallbackComposerId;
        var match = db.Composers.Local
            .FirstOrDefault(c => string.Equals(c.Name, subpieceComposer, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? fallbackComposerId;
    }

    private static void ReplaceVariantsPiece(PieceRow row, List<VariantInfo>? variants, CanonDbContext db)
    {
        foreach (var existing in row.Variants.ToList())
            db.PieceVariants.Remove(existing);
        row.Variants.Clear();
        if (variants is null) return;
        for (int i = 0; i < variants.Count; i++)
            row.Variants.Add(new PieceVariantRow
            {
                Position        = i,
                Description     = variants[i].Description,
                LongDescription = variants[i].LongDescription,
            });
    }

    private static void ReplaceVariantsVersion(PieceVersionRow row, List<VariantInfo>? variants, CanonDbContext db)
    {
        foreach (var existing in row.Variants.ToList())
            db.PieceVariants.Remove(existing);
        row.Variants.Clear();
        if (variants is null) return;
        for (int i = 0; i < variants.Count; i++)
            row.Variants.Add(new PieceVariantRow
            {
                Position        = i,
                Description     = variants[i].Description,
                LongDescription = variants[i].LongDescription,
            });
    }

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

        // Sessions ordered by Position; their zero-based index is what tracks reference.
        var orderedSessions = ar.Sessions.OrderBy(s => s.Position).ToList();
        var sessionIndexById = new Dictionary<long, int>();
        if (orderedSessions.Count > 0)
        {
            album.Sessions = new List<RecordingSession>(orderedSessions.Count);
            for (int i = 0; i < orderedSessions.Count; i++)
            {
                var sr = orderedSessions[i];
                sessionIndexById[sr.Id] = i;
                album.Sessions.Add(new RecordingSession
                {
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
                    SessionIndex  = tr.SessionId.HasValue &&
                                    sessionIndexById.TryGetValue(tr.SessionId.Value, out var si)
                                    ? si : null,
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
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Resolve track-piece refs against the live piece tree.
        var (currentPieces, pieceModelByRowId, versionModelByRowId, _, _) =
            await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);
        var rowIdByPieceModel = new Dictionary<CanonPiece, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in pieceModelByRowId) rowIdByPieceModel[m] = id;
        var rowIdByVersionModel = new Dictionary<CanonPieceVersion, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in versionModelByRowId) rowIdByVersionModel[m] = id;

        var resolver = new PieceReferenceIndex();
        resolver.BuildResolver(currentPieces);

        // Album save = full delete-and-rebuild per album. AlbumRow has no inbound
        // FKs (everything cascades from album_id), so we can delete the row and
        // every owned child in one shot, then reinsert from input.
        var existing = await db.Albums.ToListAsync().ConfigureAwait(false);
        var existingById = existing.ToDictionary(a => a.Id);
        // Mirror CanonAlbum.IdentityKey: composite over (Label, CatalogueNumber,
        // Title, Subtitle). Albums in the user's collection that lack Label /
        // CatalogueNumber (e.g. Böhm Beethoven, Bernstein Mahler) still get a
        // stable key from Title+Subtitle, so save-time dedup catches them.
        var existingByKey = new Dictionary<string, AlbumRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in existing)
        {
            var key = BuildAlbumIdentityKey(a.Label, a.CatalogueNumber, a.Title, a.Subtitle);
            if (key is not null) existingByKey[key] = a;
        }

        // Track which existing rows the input still wants — these are removed
        // here only so the second pass can re-insert them fresh. Rows that
        // *aren't* matched by any input album are orphans dropped by the user;
        // we delete them here too, so they don't reappear on next load.
        // (Album children — discs, tracks, performers, sessions, refs — all
        // cascade from album_id, so the delete is always safe.)
        var matchedExistingRowIds = new HashSet<long>();
        foreach (var album in albums)
        {
            AlbumRow? existingRow = null;
            if (_albumIds.TryGetValue(album, out var handle) && existingById.TryGetValue(handle.Id, out var hr))
                existingRow = hr;
            else if (album.IdentityKey is { } key && existingByKey.TryGetValue(key, out var kr))
                existingRow = kr;

            if (existingRow is not null)
            {
                matchedExistingRowIds.Add(existingRow.Id);
                db.Albums.Remove(existingRow);
            }
        }

        foreach (var orphan in existing.Where(a => !matchedExistingRowIds.Contains(a.Id)))
            db.Albums.Remove(orphan);

        await db.SaveChangesAsync().ConfigureAwait(false);

        var added = new List<(CanonAlbum, AlbumRow)>(albums.Count);
        foreach (var album in albums)
        {
            var row = MapAlbumModelToRow(album, resolver, rowIdByPieceModel, rowIdByVersionModel);
            db.Albums.Add(row);
            added.Add((album, row));
        }
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var (m, r) in added)
        {
            if (_albumIds.TryGetValue(m, out var h)) h.Id = r.Id;
            else _albumIds.AddOrUpdate(m, new IdHandle { Id = r.Id });
        }
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

    private static AlbumRow MapAlbumModelToRow(
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
                };
                if (track.SessionIndex is int si && sessionRowByIndex.TryGetValue(si, out var sessRow))
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

        return row;
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
