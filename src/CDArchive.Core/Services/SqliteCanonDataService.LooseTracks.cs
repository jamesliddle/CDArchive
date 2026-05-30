using System.Diagnostics;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CDArchive.Core.Services;

/// <summary>
/// Loose tracks subsystem load + save (H1 slice 4). Extracted from
/// <c>SqliteCanonDataService.cs</c> via partial-class split.
///
/// <para>Loose tracks live in the same <c>album_tracks</c> table as
/// album-bound tracks with <c>disc_id NULL</c>. Identity is tracked via the
/// <c>_looseTrackIds</c> CWT; there's no natural-key fallback so a
/// JSON-cloned loose track will insert fresh on save. The Tracks view
/// doesn't JSON-clone tracks for editing, so this is fine in practice.</para>
///
/// <para>Behaviour unchanged. The methods retain visibility, signatures, and
/// dependency surface — the private CWT <c>_looseTrackIds</c>,
/// <c>_dbFactory</c>, <c>_logger</c>, the <c>IdHandle</c> nested type,
/// <c>EnsureInitializedAsync</c>, and the Albums/Pieces partials'
/// helpers (<c>LoadAllPiecesInternalAsync</c>, <c>BuildTrackPieceRef</c>,
/// <c>MergePieceRefs</c>, <c>MergeTrackPerformers</c>,
/// <c>MapPerformerRow</c>) all stay in their original homes.</para>
/// </summary>
public partial class SqliteCanonDataService
{
    // ─────────────────────────────────────────────────────────────────────────
    // Loose tracks (no album)
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<AlbumTrack>> LoadLooseTracksAsync()
    {
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Piece-ref reconstruction needs the same maps the album load builds.
        var (_, pieceModelByRowId, versionModelByRowId, pieceRowById, versionRowById) =
            await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);
        var composerNameById = await db.Composers.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name)
            .ConfigureAwait(false);
        var markerRowById = await db.PieceMarkers.AsNoTracking()
            .ToDictionaryAsync(m => m.Id)
            .ConfigureAwait(false);

        var rows = await db.AlbumTracks
            .AsNoTracking()
            .Where(t => t.DiscId == null)
            .Include(t => t.PieceRefs)
            .Include(t => t.Performers)
            .OrderBy(t => t.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var result = new List<AlbumTrack>(rows.Count);
        foreach (var tr in rows)
        {
            var track = new AlbumTrack
            {
                // TrackNumber is meaningless for loose tracks; the row stores 0.
                // Keep the field zero on the model so callers can rely on the
                // "TrackNumber > 0 ⇒ album-bound" invariant.
                TrackNumber   = tr.TrackNumber,
                Duration      = tr.Duration,
                Description   = tr.Description,
                SparsCode     = tr.SparsCode,
                IsStereo      = tr.IsStereo,
                IsProvisional = tr.IsProvisional,
                FlacPath      = tr.FlacPath,
                Mp3Path       = tr.Mp3Path,
                // SessionId is null on loose tracks (no album, no sessions).
            };

            if (tr.Performers.Count > 0)
                track.Performers = tr.Performers
                    .OrderBy(p => p.Position)
                    .Select(MapPerformerRow)
                    .ToList();

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

            _looseTrackIds.AddOrUpdate(track, new IdHandle { Id = tr.Id });
            result.Add(track);
        }
        return result;
    }

    public async Task SaveLooseTracksAsync(List<AlbumTrack> tracks)
    {
        var __sw = Stopwatch.StartNew();
        _logger.LogInformation("SaveLooseTracks starting ({Count} input)", tracks.Count);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);

        var apply = await SaveLooseTracksCoreAsync(db, tracks).ConfigureAwait(false);

        await tx.CommitAsync().ConfigureAwait(false);
        apply();

        _logger.LogInformation("SaveLooseTracks completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Transactional body of <see cref="SaveLooseTracksAsync"/>. Caller owns
    /// the <see cref="CanonDbContext"/> and transaction. The two-stage save
    /// inside (inserts first to materialize row ids, then piece-refs/performers)
    /// is fine because both stages share the caller's transaction. See
    /// <see cref="SaveComposersCoreAsync"/> for the contract.
    /// </summary>
    private async Task<Action> SaveLooseTracksCoreAsync(CanonDbContext db, List<AlbumTrack> tracks)
    {
        // Resolver setup — same shape as SaveAlbumsAsync.
        var (currentPieces, pieceModelByRowId, versionModelByRowId, _, _) =
            await LoadAllPiecesInternalAsync(db).ConfigureAwait(false);
        var rowIdByPieceModel = new Dictionary<CanonPiece, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in pieceModelByRowId) rowIdByPieceModel[m] = id;
        var rowIdByVersionModel = new Dictionary<CanonPieceVersion, long>(ReferenceEqualityComparer.Instance);
        foreach (var (id, m) in versionModelByRowId) rowIdByVersionModel[m] = id;
        // Throwaway resolver — see SaveAlbumsCoreAsync above + Rework H7.
        var resolver = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(currentPieces);

        var existing = await db.AlbumTracks
            .Where(t => t.DiscId == null)
            .Include(t => t.PieceRefs)
            .Include(t => t.Performers)
            .ToListAsync()
            .ConfigureAwait(false);
        var existingById = existing.ToDictionary(r => r.Id);

        var matched = new Dictionary<AlbumTrack, AlbumTrackRow>(ReferenceEqualityComparer.Instance);
        var matchedExistingRowIds = new HashSet<long>();
        foreach (var track in tracks)
        {
            if (_looseTrackIds.TryGetValue(track, out var handle) &&
                existingById.TryGetValue(handle.Id, out var row))
            {
                matched[track] = row;
                matchedExistingRowIds.Add(row.Id);
            }
        }

        // Orphans: existing loose-track rows the input no longer references.
        // album_track_piece_refs and album_performers cascade from album_tracks.id.
        foreach (var orphan in existing.Where(r => !matchedExistingRowIds.Contains(r.Id)))
            db.AlbumTracks.Remove(orphan);

        var inserted = new List<(AlbumTrack model, AlbumTrackRow row)>();
        foreach (var track in tracks)
        {
            AlbumTrackRow row;
            if (matched.TryGetValue(track, out var existingRow))
            {
                row = existingRow;
                ApplyLooseTrackFields(row, track);
                MergePieceRefs(track.PieceRefs, row, resolver, rowIdByPieceModel, rowIdByVersionModel, db);
                MergeTrackPerformers(track.Performers, row, albumRow: null, db);
            }
            else
            {
                row = new AlbumTrackRow { DiscId = null };
                ApplyLooseTrackFields(row, track);
                db.AlbumTracks.Add(row);
                inserted.Add((track, row));
            }
        }

        // First pass: persist inserts so the new rows have row ids before we
        // attach piece-refs / performers (those FKs need a real track_id).
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var (model, row) in inserted)
        {
            MergePieceRefs(model.PieceRefs, row, resolver, rowIdByPieceModel, rowIdByVersionModel, db);
            MergeTrackPerformers(model.Performers, row, albumRow: null, db);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return () =>
        {
            foreach (var (model, row) in matched.Select(kv => (kv.Key, kv.Value)).Concat(inserted))
            {
                if (_looseTrackIds.TryGetValue(model, out var h)) h.Id = row.Id;
                else _looseTrackIds.AddOrUpdate(model, new IdHandle { Id = row.Id });
            }
        };
    }

    private static void ApplyLooseTrackFields(AlbumTrackRow row, AlbumTrack model)
    {
        // DiscId stays null — that's what marks the row as loose.
        row.TrackNumber   = model.TrackNumber;  // typically 0; meaningless without a disc
        row.Duration      = model.Duration;
        row.Description   = model.Description;
        row.SparsCode     = model.SparsCode;
        row.IsStereo      = model.IsStereo;
        row.IsProvisional = model.IsProvisional;
        row.FlacPath      = model.FlacPath;
        row.Mp3Path       = model.Mp3Path;
        // SessionId stays null — loose tracks have no album sessions.
        row.SessionId     = null;
    }
}
