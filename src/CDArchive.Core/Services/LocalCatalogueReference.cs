using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Stub for a future user-maintained catalogue database.
/// This will eventually be the authoritative source of truth for the user's own
/// cataloguing conventions — preferred name spellings, work titles, catalogue counts, etc.
/// </summary>
public class LocalCatalogueReference : ICatalogueReference
{
    public string SourceName => "Local Catalogue";

    // TODO: Back this with a persistent store (SQLite, JSON file, etc.)
    // The local catalogue will hold:
    //   - Composer entries with the user's preferred name/date formatting
    //   - Work entries with canonical titles and movement lists
    //   - Catalogue counts per work type per composer (e.g., Haydn: 104 symphonies)
    //   - User overrides that take precedence over MusicBrainz or iTunes data

    public Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null)
    {
        return Task.FromResult<ComposerInfo?>(null);
    }

    public Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm)
    {
        return Task.FromResult<WorkInfo?>(null);
    }
}
