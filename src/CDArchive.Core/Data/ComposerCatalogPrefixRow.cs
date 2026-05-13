namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>composer_catalog_prefixes</c>. Ordered list of catalogue
/// prefixes for a composer in preference order (first = primary). Used by
/// <c>CanonPiece.SortCatalogInfoByPreference</c>.
/// </summary>
public class ComposerCatalogPrefixRow
{
    public long Id { get; set; }

    public long ComposerId { get; set; }
    public ComposerRow Composer { get; set; } = null!;

    public int Position { get; set; }
    public string Prefix { get; set; } = "";
}
