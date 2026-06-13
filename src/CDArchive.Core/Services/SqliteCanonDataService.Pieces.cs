using System.Diagnostics;
using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CDArchive.Core.Services;

/// <summary>
/// Pieces subsystem load + save (H1 slice 3 — the biggest single slice
/// in the arc). Extracted from <c>SqliteCanonDataService.cs</c> via
/// partial-class split. Behaviour is unchanged; the methods retain
/// visibility, signatures, and dependency surface from the main file
/// (CWTs <c>_pieceIds</c> / <c>_versionIds</c>, <c>_dbFactory</c>,
/// <c>_logger</c>, the <c>IdHandle</c> nested type,
/// <c>EnsureInitializedAsync</c>).
///
/// <para>What lives here:</para>
/// <list type="bullet">
///   <item>Public Load + Save methods.</item>
///   <item><c>LoadAllPiecesInternalAsync</c> — the rich single-trip
///     loader that builds the model tree + row-by-id maps used by the
///     Albums subsystem to resolve track refs.</item>
///   <item><c>SavePiecesCoreAsync</c> — the transactional body called
///     by <c>SaveBatchAsync</c>.</item>
///   <item><c>UpsertPieceTree</c> + <c>UpsertVersion</c> — the
///     recursive piece-tree reconciliation engine.</item>
///   <item>Field-application helpers (<c>ApplyPieceFields</c>,
///     <c>ApplyVersionFields</c>), inner-collection Replace helpers
///     (CatalogEntries, Markers, Credits, Variants — each split per
///     owner), and the Marker reconciliation walker
///     (<c>ReconcileMarkers</c>, <c>ReconcileSubMarkers</c>,
///     <c>RemoveMarkerTree</c>).</item>
///   <item>Composer-id resolvers used by the Credits + subpiece
///     paths.</item>
///   <item>The <c>NormalizeInheritedComposers</c> post-load walker that
///     clears redundant subpiece composers, and shallow row→model
///     mappers (<c>MapPieceRowToModelShallow</c>,
///     <c>MapVersionRowToModelShallow</c>, etc.).</item>
///   <item>The <c>ParseJsonElement</c> / <c>RawJson</c> JSON-blob
///     helpers — only consumed by Pieces and Version mappings.</item>
/// </list>
/// </summary>
public partial class SqliteCanonDataService
{
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
            MusicBrainzWorkId       = r.MusicBrainzWorkId,

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
        Id                      = r.Id,
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
        Id              = r.Id,
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
        var __sw = Stopwatch.StartNew();
        _logger.LogInformation("SavePieces starting ({Count} input)", pieces.Count);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var apply = await SavePiecesCoreAsync(db, pieces).ConfigureAwait(false);
        apply();

        _logger.LogInformation("SavePieces completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Transactional body of <see cref="SavePiecesAsync"/> — see the docstring
    /// on <see cref="SaveComposersCoreAsync"/> for the contract. Used by the
    /// public method and by <see cref="SaveBatchAsync"/>.
    /// </summary>
    private async Task<Action> SavePiecesCoreAsync(CanonDbContext db, List<CanonPiece> pieces)
    {
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

        // Some of the UpsertPieceTree calls above may have queued subpiece
        // deletes (orphans whose parent is being edited — a movement
        // removed from a Sonata's subpiece list, e.g. the "1. Allegro"
        // duplicate from a previous broken import). Those go through the
        // first SaveChanges below. The album_track_piece_refs FK is
        // OnDelete:Restrict, so if any album track still references one
        // of those subpieces the delete fails with SQLite Error 19 —
        // pre-fix the user saw "FOREIGN KEY constraint failed" and the
        // app died (the dispatcher killed the process). Mirror the
        // top-level delete's catch (M3) so the failure surfaces a
        // human-readable message naming what's blocking it.
        var pendingPieceDeleteIds = db.ChangeTracker.Entries<PieceRow>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.Id != 0)
            .Select(e => e.Entity.Id)
            .ToList();

        // Pre-flight: a variant the user removed from a piece (so ReconcileVariants
        // queued its row for deletion) may still be identified on an album-track
        // recording. album_track_piece_ref_variants.variant_id is OnDelete:Restrict,
        // so the SaveChanges below would fail with a raw "FOREIGN KEY constraint
        // failed". Catch it here and surface which variants are blocking, the same
        // friendly-diagnostic style as the piece-delete paths (M3).
        var pendingVariantDeleteIds = db.ChangeTracker.Entries<PieceVariantRow>()
            .Where(e => e.State == EntityState.Deleted && e.Entity.Id != 0)
            .Select(e => e.Entity.Id)
            .ToList();
        if (pendingVariantDeleteIds.Count > 0)
        {
            var blockedVariantIds = await db.AlbumTrackPieceRefVariants.AsNoTracking()
                .Where(x => pendingVariantDeleteIds.Contains(x.VariantId))
                .Select(x => x.VariantId)
                .Distinct()
                .ToListAsync().ConfigureAwait(false);
            if (blockedVariantIds.Count > 0)
            {
                // Detach the doomed variant deletes so a retry in the same scope
                // doesn't re-trigger the same failure.
                foreach (var entry in db.ChangeTracker.Entries<PieceVariantRow>()
                                        .Where(e => e.State == EntityState.Deleted &&
                                                    blockedVariantIds.Contains(e.Entity.Id))
                                        .ToList())
                    entry.State = EntityState.Unchanged;

                var descs = await db.PieceVariants.AsNoTracking()
                    .Where(v => blockedVariantIds.Contains(v.Id))
                    .Select(v => v.Description)
                    .ToListAsync().ConfigureAwait(false);
                var names = string.Join(", ",
                    descs.Where(d => !string.IsNullOrEmpty(d)).Select(d => $"'{d}'").Take(3));
                var nameClause = names.Length > 0 ? $" ({names}…)" : "";

                throw new InvalidOperationException(
                    $"Cannot delete {blockedVariantIds.Count} variant(s){nameClause} — they are still " +
                    "identified on one or more album track recordings. Clear those variant " +
                    "selections first.");
            }
        }

        try
        {
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (pendingPieceDeleteIds.Count > 0)
        {
            // Detach the doomed deletes so subsequent save attempts in the
            // same scope don't re-trigger the same constraint failure.
            foreach (var entry in db.ChangeTracker.Entries<PieceRow>()
                                    .Where(e => e.State == EntityState.Deleted &&
                                                pendingPieceDeleteIds.Contains(e.Entity.Id))
                                    .ToList())
            {
                entry.State = EntityState.Unchanged;
            }

            // Count blocking album track refs the same way the top-level
            // delete path does (M3): break out start-refs vs range-end
            // markers so the user knows which kind to chase.
            var startRefCount = await db.AlbumTrackPieceRefs.AsNoTracking()
                .Where(r => pendingPieceDeleteIds.Contains(r.PieceId))
                .CountAsync().ConfigureAwait(false);
            var endRefCount = await db.AlbumTrackPieceRefs.AsNoTracking()
                .Where(r => r.EndPieceId != null &&
                            pendingPieceDeleteIds.Contains(r.EndPieceId.Value))
                .CountAsync().ConfigureAwait(false);

            var doomedTitles = await db.Pieces.AsNoTracking()
                .Where(p => pendingPieceDeleteIds.Contains(p.Id))
                .Select(p => p.Title)
                .ToListAsync().ConfigureAwait(false);
            var titles = string.Join(", ",
                doomedTitles.Where(t => !string.IsNullOrEmpty(t))
                            .Select(t => $"'{t}'")
                            .Take(3));

            var reasons = new List<string>();
            if (startRefCount > 0) reasons.Add($"{startRefCount} as the piece itself");
            if (endRefCount   > 0) reasons.Add($"{endRefCount} as a range-end marker");
            var reasonClause = reasons.Count > 0
                ? $" ({string.Join("; ", reasons)})"
                : "";

            var titleClause = titles.Length > 0 ? $" ({titles}…)" : "";
            throw new InvalidOperationException(
                $"Cannot delete {pendingPieceDeleteIds.Count} piece(s){titleClause} — they are still " +
                $"referenced by album track refs{reasonClause}. Remove those album references first.",
                ex);
        }

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

                // M3: distinguish "ref starts at this piece" (piece_id FK) from
                // "ref ends at this piece" (end_piece_id FK — range marker).
                // SQLite's FK Restrict message just says "FOREIGN KEY constraint
                // failed" without naming the column, but both columns target
                // album_track_piece_refs with OnDelete:Restrict, so we count
                // each and report which kind blocked the delete. The user
                // otherwise sees "remove album refs" and looks for visible
                // references that aren't there — the start-refs were stripped
                // by the cascade beforehand, and the lingering refs are the
                // range-end markers (where their EndSubpiecePath, not
                // SubpiecePath, walks into this piece).
                var doomedIds = toDelete.Select(r => r.Id).ToList();
                var startRefCount = await db.AlbumTrackPieceRefs.AsNoTracking()
                    .Where(r => doomedIds.Contains(r.PieceId))
                    .CountAsync().ConfigureAwait(false);
                var endRefCount = await db.AlbumTrackPieceRefs.AsNoTracking()
                    .Where(r => r.EndPieceId != null && doomedIds.Contains(r.EndPieceId.Value))
                    .CountAsync().ConfigureAwait(false);

                var titles = string.Join(", ",
                    toDelete.Select(r => $"'{r.Title}'").Where(s => s.Length > 2).Take(3));

                var reasons = new List<string>();
                if (startRefCount > 0) reasons.Add($"{startRefCount} as the piece itself");
                if (endRefCount   > 0) reasons.Add($"{endRefCount} as a range-end marker");
                var reasonClause = reasons.Count > 0
                    ? $" ({string.Join("; ", reasons)})"
                    : "";

                throw new InvalidOperationException(
                    $"Cannot delete {toDelete.Count} piece(s) ({titles}…) — they are still " +
                    $"referenced by album track refs{reasonClause}. Remove those album references first.",
                    ex);
            }
            // CWT entries (_pieceIds) for the deleted CanonPieces clean themselves
            // up when the in-memory models become unreachable (ConditionalWeakTable
            // is GC-aware) — no explicit removal required here.
        }

        // Update CWT for every piece / version we touched — deferred until the
        // caller commits so a rolled-back batch doesn't leave stale row ids.
        return () =>
        {
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
        };
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
        row.MusicBrainzWorkId       = m.MusicBrainzWorkId;

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
        // Snapshot the original list before pass 1 mutates `existing` via
        // attachToPiece. Without this, pass 2's orphan walk picks up the
        // freshly-added markers — their Id is still 0 (or EF's temporary
        // value once detect-changes catches them via the navigation
        // property) and not in keepIds, so RemoveMarkerTree tries to Delete
        // a never-persisted row and EF throws InvalidOperationException
        // ("temporary value while attempting to change the entity's state
        // to 'Deleted'"). Mirrors the pattern in ReconcileSubMarkers below.
        var originalExisting = existing.ToList();
        var existingById = originalExisting.ToDictionary(m => m.Id);
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

        // Pass 2: remove anything in the original list not retained.
        // Have to traverse subtrees so nested rows get explicitly Remove()d
        // — the CHECK constraint refuses null-FK orphans (same trap as tempos).
        foreach (var orphan in originalExisting)
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
    /// rationale as <c>RemoveTempoTree</c> (now retired): the multi-owner
    /// CHECK constraint rejects null-FK orphans, so explicit removes are
    /// required.
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

    // ── Variants ───────────────────────────────────────────────────────────
    // Variants must preserve stable IDs across saves so album-track refs that
    // identify which variant a recording uses keep resolving. Reconcile by id
    // (same contract as markers): input variants with a non-zero Id update the
    // existing row in place; Id == 0 becomes a new row; existing rows whose id
    // doesn't appear in the input are removed.

    private static void ReplaceVariantsPiece(PieceRow row, List<VariantInfo>? variants, CanonDbContext db)
    {
        ReconcileVariants(row.Variants, variants, db,
            attachToOwner: v => row.Variants.Add(v));
    }

    private static void ReplaceVariantsVersion(PieceVersionRow row, List<VariantInfo>? variants, CanonDbContext db)
    {
        ReconcileVariants(row.Variants, variants, db,
            attachToOwner: v => row.Variants.Add(v));
    }

    /// <summary>
    /// Reconciles a list of existing <see cref="PieceVariantRow"/>s against an
    /// incoming list of <see cref="VariantInfo"/>s, preserving row IDs by
    /// matching on <see cref="VariantInfo.Id"/>. Removes orphans, updates
    /// matched rows in place, and adds genuinely new rows. The
    /// <paramref name="attachToOwner"/> callback adds new rows to the owning
    /// piece/version's navigation collection so EF assigns the correct FK
    /// (and satisfies the exactly-one-owner CHECK constraint) on save.
    /// </summary>
    private static void ReconcileVariants(
        List<PieceVariantRow> existing,
        List<VariantInfo>? incoming,
        CanonDbContext db,
        Action<PieceVariantRow> attachToOwner)
    {
        // Snapshot before pass 1 mutates `existing` via attachToOwner — same
        // trap as ReconcileMarkers: without the snapshot, pass 2's orphan walk
        // would pick up the freshly-added (temporary-id) rows and try to Delete
        // a never-persisted entity, which EF rejects.
        var originalExisting = existing.ToList();
        var existingById = originalExisting.ToDictionary(v => v.Id);
        var keepIds = new HashSet<long>();

        if (incoming is not null)
        {
            for (int i = 0; i < incoming.Count; i++)
            {
                var src = incoming[i];
                PieceVariantRow rowVar;
                if (src.Id != 0 && existingById.TryGetValue(src.Id, out var matched))
                {
                    rowVar = matched;
                    keepIds.Add(rowVar.Id);
                }
                else
                {
                    rowVar = new PieceVariantRow();
                    attachToOwner(rowVar);
                }

                rowVar.Position        = i;
                rowVar.Description     = src.Description;
                rowVar.LongDescription = src.LongDescription;
            }
        }

        foreach (var orphan in originalExisting)
        {
            if (keepIds.Contains(orphan.Id)) continue;
            db.PieceVariants.Remove(orphan);
            existing.Remove(orphan);
        }
    }
}
