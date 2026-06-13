using System.Diagnostics;
using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CDArchive.Core.Services;

/// <summary>
/// Albums subsystem load + save + singleton-album-to-loose-track promotion
/// (H1 slice 5 — the biggest remaining slice). Extracted from
/// <c>SqliteCanonDataService.cs</c> via partial-class split.
///
/// <para>What lives here:</para>
/// <list type="bullet">
///   <item>Public <c>LoadAlbumsAsync</c> + the deep row→model mapper
///         <c>MapAlbumRowToModel</c>.</item>
///   <item>Public <c>SaveAlbumsAsync</c> + the transactional body
///         <c>SaveAlbumsCoreAsync</c> (called by both the public method and
///         <c>SaveBatchAsync</c> in the main file).</item>
///   <item>The load-mutate-save merge engine: <c>MergeAlbumIntoRow</c>,
///         <c>PlanVolumes</c>, <c>PlanSessions</c>, <c>MergeAlbumLevelPerformers</c>,
///         <c>MergeDiscs</c>, <c>MergeTracks</c>, <c>MergeTrackPerformers</c>,
///         <c>MergePieceRefs</c>, <c>ResolvePieceRef</c>, <c>ResolveSessionRow</c>,
///         <c>BackPropagateSessionIds</c>, <c>BuildAlbumIdentityKey</c>, and the
///         fresh-insert path <c>MapAlbumModelToRow</c>.</item>
///   <item>Row-translation helpers shared with the LooseTracks partial:
///         <c>MapPerformerRow</c>, <c>MapPerformerModelToRow</c>,
///         <c>BuildTrackPieceRef</c>, <c>WalkUpToTop</c>,
///         <c>BuildMarkerReference</c>, <c>ComputeNumberedDefault</c>,
///         <c>SerializeStringList</c>, <c>DeserializeStringList</c>. They live
///         here because the album load is their primary user; LooseTracks
///         consumes them via the shared partial-class state.</item>
///   <item>Singleton-album promotion: <c>PromoteSingletonAlbumsToLooseTracksAsync</c>
///         + <c>ClassifyForPromotion</c> + the raw-SQL helper <c>ExecParamAsync</c>
///         (only used by promotion).</item>
/// </list>
///
/// <para>Behaviour unchanged. Methods retain their visibility, signatures, and
/// dependency surface — the private CWT <c>_albumIds</c>, <c>_dbFactory</c>,
/// <c>_logger</c>, the <c>IdHandle</c> nested type, the JSON
/// <c>ReadOptions</c>/<c>WriteOptions</c>, <c>EnsureInitializedAsync</c>, and
/// the Pieces partial's <c>LoadAllPiecesInternalAsync</c> all stay accessible
/// via the shared partial-class state.</para>
/// </summary>
public partial class SqliteCanonDataService
{
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

        // Variants indexed by id so BuildTrackPieceRef can resolve a ref's
        // variant join rows back to descriptions (fallback matcher on import).
        var variantRowById = await db.PieceVariants.AsNoTracking()
            .ToDictionaryAsync(v => v.Id)
            .ConfigureAwait(false);

        var albumRows = await db.Albums
            .AsNoTracking()
            .Include(a => a.Volumes)
            .Include(a => a.Performers)
            .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.PieceRefs).ThenInclude(r => r.Variants)
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
                                           composerNameById, markerRowById, variantRowById);
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
        Dictionary<long, PieceMarkerRow> markerRowById,
        Dictionary<long, PieceVariantRow> variantRowById)
    {
        var album = new CanonAlbum
        {
            Title            = ar.Title,
            Subtitle         = ar.Subtitle,
            Label            = ar.Label,
            CatalogueNumber  = ar.CatalogueNumber,
            Barcode          = ar.Barcode,
            SparsCode        = ar.SparsCode,
            IsStereo         = ar.IsStereo,
            Notes            = ar.Notes,
            ArchiveFolder    = ar.ArchiveFolder,
            IsProvisional    = ar.IsProvisional,
            MusicBrainzReleaseId = ar.MusicBrainzReleaseId,
            SessionDates     = ar.SessionDates,
            SessionVenue     = ar.SessionVenue,
            SessionCity      = ar.SessionCity,
            SessionState     = ar.SessionState,
            SessionCountry   = ar.SessionCountry,
            SessionEngineers = DeserializeStringList(ar.SessionEngineersJson),
            SessionProducers = DeserializeStringList(ar.SessionProducersJson),
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
                    TrackNumber      = tr.TrackNumber,
                    Duration         = tr.Duration,
                    Description      = tr.Description,
                    SparsCode        = tr.SparsCode,
                    IsStereo         = tr.IsStereo,
                    IsProvisional    = tr.IsProvisional,
                    FlacPath         = tr.FlacPath,
                    Mp3Path          = tr.Mp3Path,
                    SessionDates     = tr.SessionDates,
                    SessionVenue     = tr.SessionVenue,
                    SessionCity      = tr.SessionCity,
                    SessionState     = tr.SessionState,
                    SessionCountry   = tr.SessionCountry,
                    SessionEngineers = DeserializeStringList(tr.SessionEngineersJson),
                    SessionProducers = DeserializeStringList(tr.SessionProducersJson),
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
                            composerNameById, markerRowById, variantRowById);
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
        Dictionary<long, PieceMarkerRow> markerRowById,
        Dictionary<long, PieceVariantRow> variantRowById)
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
            VersionId          = refRow.VersionId ?? 0,
            DisplayLabel       = refRow.DisplayLabel,
            StartMarker        = BuildMarkerReference(refRow.StartMarkerId, markerRowById),
            EndMarker          = BuildMarkerReference(refRow.EndMarkerId,   markerRowById),
            Variants           = BuildVariantReferences(refRow.Variants, variantRowById),
        };
        return trackRef;
    }

    /// <summary>
    /// Rebuilds the <see cref="VariantReference"/> list from a ref's join rows,
    /// carrying each variant's description as a fallback matcher. Returns null
    /// when the ref identifies no variants (the common case).
    /// </summary>
    private static List<VariantReference>? BuildVariantReferences(
        List<AlbumTrackPieceRefVariantRow> joinRows,
        Dictionary<long, PieceVariantRow> variantRowById)
    {
        if (joinRows is not { Count: > 0 }) return null;
        var list = new List<VariantReference>(joinRows.Count);
        foreach (var jr in joinRows.OrderBy(x => x.Position))
        {
            variantRowById.TryGetValue(jr.VariantId, out var vr);
            list.Add(new VariantReference
            {
                Id          = jr.VariantId,
                Description = vr?.Description,
            });
        }
        return list.Count > 0 ? list : null;
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
                .Include(a => a.Performers)
                .Include(a => a.Discs).ThenInclude(d => d.Tracks).ThenInclude(t => t.PieceRefs).ThenInclude(r => r.Variants)
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
        // Session-as-fields refactor: no more session map / back-propagation —
        // session fields live directly on each album / track row, so
        // SaveChanges flushes them with no post-save bookkeeping needed.
        foreach (var album in albums)
        {
            if (matchedRowIdByModel.TryGetValue(album, out var rowId) &&
                matchedRows.TryGetValue(rowId, out var row))
            {
                MergeAlbumIntoRow(album, row, resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                matched[album] = row;
            }
            else
            {
                var newRow = MapAlbumModelToRow(album, resolver, rowIdByPieceModel, rowIdByVersionModel);
                db.Albums.Add(newRow);
                inserted.Add((album, newRow));
            }
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

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
            //    sentinel). The session_id column was retired in the
            //    sessions-as-fields refactor — the session_* columns are
            //    already on the track row from the migration; no extra work
            //    here.
            await ExecParamAsync(conn, tx,
                "UPDATE album_tracks SET disc_id = NULL, track_number = 0, " +
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
        if (HasAnySessionFields(album))                                    return "has session details";
        if (!string.IsNullOrWhiteSpace(album.Label))                       return $"has Label='{album.Label}'";
        if (!string.IsNullOrWhiteSpace(album.CatalogueNumber))             return $"has CatalogueNumber='{album.CatalogueNumber}'";
        if (!string.IsNullOrWhiteSpace(album.Barcode))                     return "has Barcode";
        if (!string.IsNullOrWhiteSpace(album.ArchiveFolder))               return "has ArchiveFolder";
        if (album.Discs[0].Tracks[0].Performers.Count > 0)                 return "track has track-level Performers (manual override?)";
        return null;
    }

    private static bool HasAnySessionFields(AlbumRow album) =>
        !string.IsNullOrWhiteSpace(album.SessionDates) ||
        !string.IsNullOrWhiteSpace(album.SessionVenue) ||
        !string.IsNullOrWhiteSpace(album.SessionCity) ||
        !string.IsNullOrWhiteSpace(album.SessionState) ||
        !string.IsNullOrWhiteSpace(album.SessionCountry) ||
        !string.IsNullOrEmpty(album.SessionEngineersJson) ||
        !string.IsNullOrEmpty(album.SessionProducersJson);

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

    private static void MergeAlbumIntoRow(
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
        row.MusicBrainzReleaseId = album.MusicBrainzReleaseId;

        // Session fields: post-refactor these are flat columns on AlbumRow.
        row.SessionDates          = album.SessionDates;
        row.SessionVenue          = album.SessionVenue;
        row.SessionCity           = album.SessionCity;
        row.SessionState          = album.SessionState;
        row.SessionCountry        = album.SessionCountry;
        row.SessionEngineersJson  = SerializeStringList(album.SessionEngineers);
        row.SessionProducersJson  = SerializeStringList(album.SessionProducers);

        // Volume orphan deletes are deferred until AFTER disc rewiring so the
        // disc.Volume FK is repointed before its current parent is dropped
        // (FK Restrict would otherwise fail). Sessions used to have the same
        // dance for track.Session FKs, but that FK is gone post-refactor.
        var (volumeMap, orphanVolumes) = PlanVolumes(album.Volumes, row);

        MergeAlbumLevelPerformers(album.Performers, row, db);

        MergeDiscs(album.Discs, row, volumeMap,
                   resolver, rowIdByPieceModel, rowIdByVersionModel, db);

        foreach (var v in orphanVolumes) db.AlbumVolumes.Remove(v);
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

    // PlanSessions retired in the sessions-as-fields refactor — session
    // fields live directly on AlbumRow and are written in MergeAlbumIntoRow.

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
                MergeTracks(inputDisc.Tracks, dr,
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
                MergeTracks(inputDisc.Tracks, fresh,
                            resolver, rowIdByPieceModel, rowIdByVersionModel, row, db);
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumDiscs.Remove(orphan);
    }

    /// <summary>
    /// Tracks match by TrackNumber within their disc. Mirrors the disc merge:
    /// scalar fields (now including the 7 session_* columns), then recurses
    /// into per-track piece-refs and per-track performers.
    /// </summary>
    private static void MergeTracks(
        List<AlbumTrack> input,
        AlbumDiscRow disc,
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
            if (existingByNumber.TryGetValue(inputTrack.TrackNumber, out var tr))
            {
                ApplyTrackScalars(tr, inputTrack);
                MergePieceRefs(inputTrack.PieceRefs, tr,
                               resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                MergeTrackPerformers(inputTrack.Performers, tr, albumRow, db);
                matched.Add(tr.Id);
            }
            else
            {
                var fresh = new AlbumTrackRow { TrackNumber = inputTrack.TrackNumber };
                ApplyTrackScalars(fresh, inputTrack);
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
    /// Copies the model's scalar + session fields onto an <see cref="AlbumTrackRow"/>.
    /// Shared between matched-update and fresh-insert paths in <see cref="MergeTracks"/>
    /// and used by the loose-track path in the sibling LooseTracks partial.
    /// </summary>
    internal static void ApplyTrackScalars(AlbumTrackRow tr, AlbumTrack inputTrack)
    {
        tr.Duration              = inputTrack.Duration;
        tr.Description           = inputTrack.Description;
        tr.SparsCode             = inputTrack.SparsCode;
        tr.IsStereo              = inputTrack.IsStereo;
        tr.IsProvisional         = inputTrack.IsProvisional;
        tr.FlacPath              = inputTrack.FlacPath;
        tr.Mp3Path               = inputTrack.Mp3Path;
        tr.SessionDates          = inputTrack.SessionDates;
        tr.SessionVenue          = inputTrack.SessionVenue;
        tr.SessionCity           = inputTrack.SessionCity;
        tr.SessionState          = inputTrack.SessionState;
        tr.SessionCountry        = inputTrack.SessionCountry;
        tr.SessionEngineersJson  = SerializeStringList(inputTrack.SessionEngineers);
        tr.SessionProducersJson  = SerializeStringList(inputTrack.SessionProducers);
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

                AlbumTrackPieceRefRow refRow;
                if (existingByPosition.TryGetValue(slot, out var er))
                {
                    er.PieceId       = r.PieceId;
                    er.VersionId     = r.VersionId;
                    er.EndPieceId    = r.EndPieceId;
                    er.StartMarkerId = r.StartMarkerId;
                    er.EndMarkerId   = r.EndMarkerId;
                    er.DisplayLabel  = r.DisplayLabel;
                    matched.Add(er.Id);
                    refRow = er;
                }
                else
                {
                    refRow = new AlbumTrackPieceRefRow
                    {
                        Position      = slot,
                        PieceId       = r.PieceId,
                        VersionId     = r.VersionId,
                        EndPieceId    = r.EndPieceId,
                        StartMarkerId = r.StartMarkerId,
                        EndMarkerId   = r.EndMarkerId,
                        DisplayLabel  = r.DisplayLabel,
                    };
                    track.PieceRefs.Add(refRow);
                }
                MergeRefVariants(refRow, r.VariantIds, db);
                slot++;
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !matched.Contains(orphan.Id))
                db.AlbumTrackPieceRefs.Remove(orphan);
    }

    /// <summary>
    /// Reconciles a ref's variant join rows in place against the desired
    /// <c>piece_variants</c> ids. Matched rows are kept (position updated),
    /// new ids are added, and join rows whose variant is no longer identified
    /// are removed. Deleting a join row never trips the variant-side Restrict
    /// FK — the join row owns that FK, it isn't its target.
    /// </summary>
    private static void MergeRefVariants(
        AlbumTrackPieceRefRow refRow, IReadOnlyList<long> desired, CanonDbContext db)
    {
        var existing = refRow.Variants.ToList();
        var existingByVariantId = new Dictionary<long, AlbumTrackPieceRefVariantRow>();
        foreach (var x in existing) existingByVariantId.TryAdd(x.VariantId, x);
        var keep = new HashSet<long>();

        for (int i = 0; i < desired.Count; i++)
        {
            var vid = desired[i];
            if (existingByVariantId.TryGetValue(vid, out var jr))
            {
                jr.Position = i;
                keep.Add(jr.Id);
            }
            else
            {
                refRow.Variants.Add(new AlbumTrackPieceRefVariantRow
                {
                    VariantId = vid,
                    Position  = i,
                });
            }
        }

        foreach (var orphan in existing)
            if (orphan.Id != 0 && !keep.Contains(orphan.Id))
                db.AlbumTrackPieceRefVariants.Remove(orphan);
    }

    /// <summary>
    /// Resolves a <see cref="TrackPieceRef"/> against the live piece tree,
    /// returning the row-id bundle to write into an <see cref="AlbumTrackPieceRefRow"/>.
    /// Returns null when the ref doesn't resolve (caller skips the row, matching
    /// the original behavior — unresolved refs are dropped silently at save
    /// time; the seeder is the path that surfaces them).
    /// </summary>
    private readonly record struct PieceRefResolution(
        long          PieceId,
        long?         VersionId,
        long?         EndPieceId,
        long?         StartMarkerId,
        long?         EndMarkerId,
        string?       DisplayLabel,
        IReadOnlyList<long> VariantIds);

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
            pieceRef.DisplayLabel,
            ResolveVariantRowIds(pieceRef, resolver));
    }

    /// <summary>
    /// Resolves a ref's <see cref="VariantReference"/> list to <c>piece_variants</c>
    /// row ids, validated against the variants actually available on the resolved
    /// path (leaf + ancestors + version). Each reference resolves by id first,
    /// falling back to description; unresolvable / off-path references are dropped.
    /// Duplicates are collapsed, original order preserved.
    /// </summary>
    private static IReadOnlyList<long> ResolveVariantRowIds(
        TrackPieceRef pieceRef, PieceReferenceIndex resolver)
    {
        if (pieceRef.Variants is not { Count: > 0 }) return Array.Empty<long>();

        var available = resolver.CollectAvailableVariants(pieceRef);
        if (available.Count == 0) return Array.Empty<long>();

        var ids = new List<long>(pieceRef.Variants.Count);
        var seen = new HashSet<long>();
        foreach (var vr in pieceRef.Variants)
        {
            VariantInfo? match = vr.Id != 0
                ? available.FirstOrDefault(a => a.Id == vr.Id)
                : null;
            match ??= !string.IsNullOrWhiteSpace(vr.Description)
                ? available.FirstOrDefault(a =>
                      string.Equals(a.Description, vr.Description, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match is { Id: > 0 } && seen.Add(match.Id))
                ids.Add(match.Id);
        }
        return ids;
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
            Title                 = album.Title,
            Subtitle              = album.Subtitle,
            Label                 = album.Label,
            CatalogueNumber       = album.CatalogueNumber,
            Barcode               = album.Barcode,
            SparsCode             = album.SparsCode,
            IsStereo              = album.IsStereo,
            Notes                 = album.Notes,
            ArchiveFolder         = album.ArchiveFolder,
            IsProvisional         = album.IsProvisional,
            MusicBrainzReleaseId  = album.MusicBrainzReleaseId,
            SessionDates          = album.SessionDates,
            SessionVenue          = album.SessionVenue,
            SessionCity           = album.SessionCity,
            SessionState          = album.SessionState,
            SessionCountry        = album.SessionCountry,
            SessionEngineersJson  = SerializeStringList(album.SessionEngineers),
            SessionProducersJson  = SerializeStringList(album.SessionProducers),
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
                var tr = new AlbumTrackRow { TrackNumber = track.TrackNumber };
                ApplyTrackScalars(tr, track);

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

                        var refRow = new AlbumTrackPieceRefRow
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
                        };
                        var variantIds = ResolveVariantRowIds(pieceRef, resolver);
                        for (int vi = 0; vi < variantIds.Count; vi++)
                            refRow.Variants.Add(new AlbumTrackPieceRefVariantRow
                            {
                                VariantId = variantIds[vi],
                                Position  = vi,
                            });
                        tr.PieceRefs.Add(refRow);
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
