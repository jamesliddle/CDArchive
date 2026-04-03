using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Chains multiple ICatalogueReference sources in priority order.
/// Queries each source in sequence and returns the first hit.
/// Default order: Local Catalogue → iTunes Library → MusicBrainz.
/// </summary>
public class CompositeCatalogueReference : ICatalogueReference
{
    public string SourceName => "Composite";

    private readonly IReadOnlyList<ICatalogueReference> _sources;
    private string? _lastSourceUsed;

    /// <summary>
    /// Which source provided the last successful result.
    /// Useful for displaying provenance to the user.
    /// </summary>
    public string? LastSourceUsed => _lastSourceUsed;

    public CompositeCatalogueReference(
        LocalCatalogueReference localCatalogue,
        ItunesLibraryReference itunesLibrary,
        MusicBrainzReference musicBrainz)
    {
        _sources = new ICatalogueReference[] { localCatalogue, itunesLibrary, musicBrainz };
    }

    public async Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null)
    {
        _lastSourceUsed = null;
        foreach (var source in _sources)
        {
            var result = await source.LookupComposerAsync(lastName, firstName);
            if (result != null)
            {
                _lastSourceUsed = source.SourceName;
                return result;
            }
        }
        return null;
    }

    public async Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm)
    {
        _lastSourceUsed = null;
        foreach (var source in _sources)
        {
            var result = await source.LookupWorkAsync(composerLastName, workSearchTerm);
            if (result != null)
            {
                _lastSourceUsed = source.SourceName;
                return result;
            }
        }
        return null;
    }
}
