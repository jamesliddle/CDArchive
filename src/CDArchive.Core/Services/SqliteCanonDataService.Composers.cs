using System.Diagnostics;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CDArchive.Core.Services;

/// <summary>
/// Composers subsystem load + save (H1 slice 1). Extracted from
/// <c>SqliteCanonDataService.cs</c> via partial-class split to bring the
/// god-class size down. Behaviour is unchanged; the methods retain
/// visibility, signatures, and dependency surface from the main file
/// (private CWT <c>_composerIds</c>, <c>_dbFactory</c>, <c>_logger</c>,
/// the <c>IdHandle</c> nested type, <c>EnsureInitializedAsync</c>).
/// </summary>
public partial class SqliteCanonDataService
{
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
        var __sw = Stopwatch.StartNew();
        _logger.LogInformation("SaveComposers starting ({Count} input)", composers.Count);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var apply = await SaveComposersCoreAsync(db, composers).ConfigureAwait(false);
        apply();

        _logger.LogInformation("SaveComposers completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// The transactional body of <see cref="SaveComposersAsync"/>. Takes an
    /// already-open <see cref="CanonDbContext"/> (and the caller's outer
    /// transaction, if any), runs the upsert + orphan-delete passes inside it,
    /// and returns the CWT-id-update closure to apply after the caller commits.
    /// Used both by the public single-subsystem method and by
    /// <see cref="SaveBatchAsync"/> where multiple Save*Core calls share one
    /// transaction.
    /// </summary>
    private async Task<Action> SaveComposersCoreAsync(CanonDbContext db, List<CanonComposer> composers)
    {
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

        // CWT updates run after the caller commits, so a rolled-back batch
        // doesn't leave stale row ids on the in-memory models.
        return () =>
        {
            foreach (var (model, row) in matched)
            {
                if (_composerIds.TryGetValue(model, out var h)) h.Id = row.Id;
                else _composerIds.AddOrUpdate(model, new IdHandle { Id = row.Id });
            }
        };
    }
}
