using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Loads and saves the Classical Canon reference data (composers and pieces JSON files).
/// </summary>
public class CanonDataService : ICanonDataService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _dataDirectory;

    public CanonDataService()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CanonDataService).Assembly.Location) ?? ".";
        _dataDirectory = FindRepoDataDirectory(assemblyDir);
    }

    public CanonDataService(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    /// <summary>
    /// Locates the repo's <c>data/</c> folder starting from <paramref name="startFrom"/>.
    /// First checks a direct <c>data/</c> subdirectory; if that doesn't exist,
    /// walks up the directory tree looking for a sibling <c>data/</c> that
    /// contains either of the dual canon-data markers — <c>Classical Canon
    /// composers.json</c> or <c>ClassicalCanon.db</c> — so the resolver still
    /// works after the JSON files have been deleted post-migration (or the
    /// other way around). Falls back to <c>&lt;startFrom&gt;/data</c> when
    /// nothing matches, which preserves the original ctor behaviour on a
    /// clean checkout. Internal so tests can drive the walk against a temp
    /// dir without touching the production <c>data/</c> directory.
    /// </summary>
    internal static string FindRepoDataDirectory(string startFrom)
    {
        var dataDir = Path.Combine(startFrom, "data");
        if (Directory.Exists(dataDir))
            return dataDir;

        var dir = new DirectoryInfo(startFrom);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate) &&
                (File.Exists(Path.Combine(candidate, "Classical Canon composers.json")) ||
                 File.Exists(Path.Combine(candidate, "ClassicalCanon.db"))))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return dataDir;
    }

    public string ComposersFilePath    => Path.Combine(_dataDirectory, "Classical Canon composers.json");
    public string PiecesFilePath       => Path.Combine(_dataDirectory, "Classical Canon pieces.json");
    public string AlbumsFilePath       => Path.Combine(_dataDirectory, "Classical Canon albums.json");
    public string PickListsFilePath    => Path.Combine(_dataDirectory, "Classical Canon pick lists.json");
    public string LooseTracksFilePath  => Path.Combine(_dataDirectory, "Classical Canon loose tracks.json");

    public async Task<List<CanonComposer>> LoadComposersAsync()
    {
        if (!File.Exists(ComposersFilePath))
            return [];

        var json = await File.ReadAllTextAsync(ComposersFilePath);
        return JsonSerializer.Deserialize<List<CanonComposer>>(json, ReadOptions) ?? [];
    }

    public async Task<List<CanonPiece>> LoadPiecesAsync()
    {
        if (!File.Exists(PiecesFilePath))
            return [];

        var json = await File.ReadAllTextAsync(PiecesFilePath);
        var pieces = JsonSerializer.Deserialize<List<CanonPiece>>(json, ReadOptions) ?? [];

        // Propagate parent catalog numbers to subpieces so they can display
        // e.g., "Op. 2 #1" instead of just "Op. #1".
        foreach (var piece in pieces)
            PropagateCatalogNumbers(piece);

        // Stage-2 migration: legacy JSON without a `markers` key needs its
        // tempos / first_line entries folded into Markers so the unified
        // anchor pipeline (resolver, picker UI) sees them. Idempotent — when
        // a piece's Markers list already contains an equivalent entry the
        // synthesis is skipped, so re-loading after `--export` (which writes
        // both shapes during the transition) doesn't double-up.
        foreach (var piece in pieces)
            MigrateLegacyAnchorsToMarkers(piece);

        return pieces;
    }

    /// <summary>
    /// Walks a piece (and its subpieces / versions / version-subpieces) and
    /// synthesises kind=Tempo entries on <see cref="CanonPiece.Markers"/>
    /// from the legacy <see cref="CanonPiece.Tempos"/> field when they aren't
    /// already represented. Mirrors the seeder's
    /// <c>SynthesizeLegacyAnchorMarkers</c> so JSON loaded directly through
    /// this service ends up in the same shape as JSON loaded via the seeder.
    /// <para>FirstLine folding was retired — the legacy <c>first_line</c> key
    /// folds into <see cref="CanonPiece.Title"/> at deserialize, so there's
    /// no first-line value left to migrate into a marker here.</para>
    /// </summary>
    private static void MigrateLegacyAnchorsToMarkers(CanonPiece piece)
    {
        piece.Markers = FoldLegacyAnchors(piece.Tempos, piece.Markers);

        if (piece.Subpieces is { Count: > 0 })
            foreach (var sub in piece.Subpieces) MigrateLegacyAnchorsToMarkers(sub);

        if (piece.Versions is { Count: > 0 })
            foreach (var v in piece.Versions)
            {
                v.Markers = FoldLegacyAnchors(v.Tempos, v.Markers);
                if (v.Subpieces is { Count: > 0 })
                    foreach (var sub in v.Subpieces) MigrateLegacyAnchorsToMarkers(sub);
            }
    }

    /// <summary>
    /// Returns a markers list with kind=Tempo entries synthesised from the
    /// legacy <c>tempos</c> field when missing, deduping on (kind, value,
    /// number) so repeated reseeds don't double up. Returns null only when
    /// nothing's accumulated.
    /// </summary>
    private static List<MusicalMarker>? FoldLegacyAnchors(
        List<TempoInfo>? tempos, List<MusicalMarker>? markers)
    {
        if (tempos is null or { Count: 0 })
            return markers;

        markers ??= [];

        foreach (var t in tempos)
        {
            if (string.IsNullOrEmpty(t.Description)) continue;
            int? num = t.Number == 0 ? null : t.Number;
            if (markers.Any(m => m.Kind == MarkerKind.Tempo &&
                string.Equals(m.Value, t.Description, StringComparison.OrdinalIgnoreCase) &&
                m.Number == num))
                continue;

            markers.Add(new MusicalMarker
            {
                Kind   = MarkerKind.Tempo,
                Value  = t.Description,
                Number = num,
            });
        }

        return markers;
    }

    /// <summary>
    /// Propagates a parent's catalog_number to subpieces that only have a catalog_subnumber,
    /// so they can display the full reference (e.g., "Op. 2 #1").
    /// </summary>
    private static void PropagateCatalogNumbers(CanonPiece parent)
    {
        if (parent.Subpieces == null) return;

        var parentCatNum = parent.CatalogInfo?.FirstOrDefault()?.CatalogNumber;

        foreach (var sub in parent.Subpieces)
        {
            if (parentCatNum != null && sub.CatalogInfo is { Count: > 0 })
            {
                var subCat = sub.CatalogInfo[0];
                if (subCat.CatalogNumber == null && subCat.CatalogSubnumber != null)
                    subCat.CatalogNumber = parentCatNum;
            }

            // Don't propagate composer to subpieces — they inherit context
            // from the tree hierarchy, and showing it on expanded lines is redundant.

            // Recurse
            PropagateCatalogNumbers(sub);
        }
    }

    public async Task SaveComposersAsync(List<CanonComposer> composers)
    {
        var sorted = composers.OrderBy(
            c => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name,
            StringComparer.OrdinalIgnoreCase).ToList();
        var json = JsonSerializer.Serialize(sorted, WriteOptions);
        await File.WriteAllTextAsync(ComposersFilePath, json);
    }

    public async Task SavePiecesAsync(List<CanonPiece> pieces)
    {
        var sorted = pieces
            .OrderBy(p => p.Composer ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.FormatCatalog(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(sorted, WriteOptions);
        await File.WriteAllTextAsync(PiecesFilePath, json);
    }

    public async Task<List<CanonAlbum>> LoadAlbumsAsync()
    {
        if (!File.Exists(AlbumsFilePath))
            return [];

        var json = await File.ReadAllTextAsync(AlbumsFilePath);
        return JsonSerializer.Deserialize<List<CanonAlbum>>(json, ReadOptions) ?? [];
    }

    public async Task SaveAlbumsAsync(List<CanonAlbum> albums)
    {
        var sorted = albums
            .OrderBy(a => a.Label ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.CatalogueNumber ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(sorted, WriteOptions);
        await File.WriteAllTextAsync(AlbumsFilePath, json);
    }

    public async Task<List<AlbumTrack>> LoadLooseTracksAsync()
    {
        if (!File.Exists(LooseTracksFilePath))
            return [];

        var json = await File.ReadAllTextAsync(LooseTracksFilePath);
        return JsonSerializer.Deserialize<List<AlbumTrack>>(json, ReadOptions) ?? [];
    }

    public async Task SaveLooseTracksAsync(List<AlbumTrack> tracks)
    {
        // Sort by description for a stable on-disk order — loose tracks have no
        // natural numeric ordering. Tracks with no description fall back to the
        // first piece-ref's display summary so re-imports don't churn the diff.
        var sorted = tracks
            .OrderBy(t => t.Description ?? FirstRefSummary(t), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = JsonSerializer.Serialize(sorted, WriteOptions);
        await File.WriteAllTextAsync(LooseTracksFilePath, json);

        static string FirstRefSummary(AlbumTrack t) =>
            t.PieceRefs is { Count: > 0 } refs ? refs[0].DisplaySummary : "";
    }

    public async Task<CanonPickLists> LoadPickListsAsync()
    {
        if (!File.Exists(PickListsFilePath))
            return new CanonPickLists();

        var json = await File.ReadAllTextAsync(PickListsFilePath);
        return JsonSerializer.Deserialize<CanonPickLists>(json, ReadOptions) ?? new CanonPickLists();
    }

    public async Task SavePickListsAsync(CanonPickLists pickLists)
    {
        // Sort each list before saving
        pickLists.Forms.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.Categories.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.CatalogPrefixes.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.KeyTonalities.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.PerformerRoles.Sort(StringComparer.OrdinalIgnoreCase);
        pickLists.Labels.Sort(StringComparer.OrdinalIgnoreCase);

        var json = JsonSerializer.Serialize(pickLists, WriteOptions);
        await File.WriteAllTextAsync(PickListsFilePath, json);
    }

    /// <summary>
    /// JSON-side batch save: writes each non-null subsystem to its file in
    /// sequence. Unlike the SQLite implementation this is best-effort — there
    /// is no cross-file atomicity primitive on a regular filesystem — but the
    /// JSON service is only used for explicit user-driven export, not for
    /// runtime persistence, so cross-save atomicity isn't load-bearing here.
    /// The contract on <see cref="ICanonDataService.SaveBatchAsync"/> is
    /// satisfied behaviourally by the SQLite implementation; this stub keeps
    /// the interface uniform.
    /// </summary>
    public async Task SaveBatchAsync(
        List<CanonComposer>? composers = null,
        List<CanonPiece>? pieces = null,
        List<CanonAlbum>? albums = null,
        List<AlbumTrack>? looseTracks = null,
        CanonPickLists? pickLists = null)
    {
        if (pickLists   is not null) await SavePickListsAsync(pickLists).ConfigureAwait(false);
        if (composers   is not null) await SaveComposersAsync(composers).ConfigureAwait(false);
        if (pieces      is not null) await SavePiecesAsync(pieces).ConfigureAwait(false);
        if (albums      is not null) await SaveAlbumsAsync(albums).ConfigureAwait(false);
        if (looseTracks is not null) await SaveLooseTracksAsync(looseTracks).ConfigureAwait(false);
    }

    /// <summary>
    /// JSON has no relational variant-reference join, so the editor's in-use
    /// check is a runtime-SQLite concern only. Returns empty — the JSON service
    /// is used for explicit import/export, never as the runtime data path.
    /// </summary>
    public Task<IReadOnlyDictionary<long, int>> GetReferencedVariantCountsAsync() =>
        Task.FromResult<IReadOnlyDictionary<long, int>>(
            new Dictionary<long, int>());
}
