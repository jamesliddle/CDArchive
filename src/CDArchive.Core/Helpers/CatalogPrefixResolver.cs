namespace CDArchive.Core.Helpers;

/// <summary>
/// Decides which catalogue prefixes the piece editor offers in its Catalogue
/// dropdown for a given composer.
/// </summary>
public static class CatalogPrefixResolver
{
    /// <summary>
    /// When the composer has a curated per-composer prefix list, that list is
    /// authoritative — returned as-is, in the user's preferred order. This is
    /// what lets a composer-specific prefix (e.g. "Anh." added to Beethoven)
    /// appear even when it isn't in the global <paramref name="globalPrefixes"/>
    /// pick list.
    ///
    /// <para>Pre-fix the editor intersected the two lists (iterating the global
    /// list, keeping only globally-known prefixes), which silently dropped any
    /// prefix the user added to the composer but not to the global pick list —
    /// the reported "Anh. doesn't show in the dropdown" bug. The per-composer
    /// list also encodes ordering preference (e.g. Op. before WoO), so using it
    /// directly is strictly better than the global-ordered intersection.</para>
    ///
    /// <para>Falls back to the full global list when the composer has no
    /// per-composer prefixes (the catalogue isn't narrowed for them yet).</para>
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        string? composerName,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? composerCatalogs,
        IReadOnlyList<string> globalPrefixes)
    {
        if (!string.IsNullOrWhiteSpace(composerName)
            && composerCatalogs is not null
            && composerCatalogs.TryGetValue(composerName.Trim(), out var permitted)
            && permitted.Count > 0)
        {
            return permitted;
        }

        return globalPrefixes;
    }
}
