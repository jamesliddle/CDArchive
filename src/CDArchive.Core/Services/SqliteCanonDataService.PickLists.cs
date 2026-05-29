using System.Diagnostics;
using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CDArchive.Core.Services;

/// <summary>
/// Pick lists subsystem load + save (H1 slice 2). Extracted from
/// <c>SqliteCanonDataService.cs</c> via partial-class split. Behaviour is
/// unchanged; the methods retain visibility, signatures, and dependency
/// surface from the main file (<c>_dbFactory</c>, <c>_logger</c>,
/// <c>EnsureInitializedAsync</c>, <c>ReadOptions</c>, <c>WriteOptions</c>).
/// </summary>
public partial class SqliteCanonDataService
{
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
        var __sw = Stopwatch.StartNew();
        var __inputCount = pickLists.Forms.Count + pickLists.Categories.Count
            + pickLists.CatalogPrefixes.Count + pickLists.KeyTonalities.Count
            + pickLists.VoiceTypes.Count + pickLists.Instruments.Count
            + (pickLists.Ensembles?.Count ?? 0);
        _logger.LogInformation("SavePickLists starting ({Count} input values)", __inputCount);
        await EnsureInitializedAsync().ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        await SavePickListsCoreAsync(db, pickLists).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogInformation("SavePickLists completed in {ElapsedMs} ms", __sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Transactional body of <see cref="SavePickListsAsync"/> — see the
    /// docstring on <see cref="SaveComposersCoreAsync"/> for the contract.
    /// Stages the pick-list delete-and-reinsert into the supplied context
    /// without calling <c>SaveChangesAsync</c>; the caller flushes. Used by the
    /// public method and by <see cref="SaveBatchAsync"/>.
    /// </summary>
    private async Task SavePickListsCoreAsync(CanonDbContext db, CanonPickLists pickLists)
    {
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
}
