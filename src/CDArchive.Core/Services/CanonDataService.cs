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
        // Default to the data/ folder relative to the assembly location,
        // but fall back to the repo data/ folder for development.
        var assemblyDir = Path.GetDirectoryName(typeof(CanonDataService).Assembly.Location) ?? ".";
        var dataDir = Path.Combine(assemblyDir, "data");
        if (!Directory.Exists(dataDir))
        {
            // Walk up to find the repo root's data/ folder
            var dir = new DirectoryInfo(assemblyDir);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "data");
                if (Directory.Exists(candidate) &&
                    File.Exists(Path.Combine(candidate, "Classical Canon composers.json")))
                {
                    dataDir = candidate;
                    break;
                }
                dir = dir.Parent;
            }
        }
        _dataDirectory = dataDir;
    }

    public CanonDataService(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
    }

    public string ComposersFilePath => Path.Combine(_dataDirectory, "Classical Canon composers.json");
    public string PiecesFilePath    => Path.Combine(_dataDirectory, "Classical Canon pieces.json");
    public string AlbumsFilePath    => Path.Combine(_dataDirectory, "Classical Canon albums.json");
    public string PickListsFilePath => Path.Combine(_dataDirectory, "Classical Canon pick lists.json");

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

        return pieces;
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

}
