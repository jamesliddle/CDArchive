using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Provides reference data for cataloguing: composer info, work details, movement titles.
/// Implementations are layered (local library → external API → user catalogue)
/// and queried in priority order by CompositeCatalogueReference.
/// </summary>
public interface ICatalogueReference
{
    string SourceName { get; }

    Task<ComposerInfo?> LookupComposerAsync(string lastName, string? firstName = null);
    Task<WorkInfo?> LookupWorkAsync(string composerLastName, string workSearchTerm);
}
