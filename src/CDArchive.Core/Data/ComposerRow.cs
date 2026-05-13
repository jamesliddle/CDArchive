namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>composers</c> table. One row per composer.
/// </summary>
public class ComposerRow
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
    public string SortName { get; set; } = "";

    public string? BirthDate { get; set; }
    public string? BirthPlace { get; set; }
    public string? BirthState { get; set; }
    public string? BirthCountry { get; set; }
    public string? BirthNotes { get; set; }

    public string? DeathDate { get; set; }
    public string? DeathPlace { get; set; }
    public string? DeathState { get; set; }
    public string? DeathCountry { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// True until the composer is explicitly approved. Defaults to true so any
    /// row inserted without an explicit value (imports, iTunes auto-creates,
    /// historical rows back-filled by the schema migration) starts provisional.
    /// </summary>
    public bool IsProvisional { get; set; } = true;

    public List<ComposerAliasRow> Aliases { get; set; } = [];
    public List<ComposerCatalogPrefixRow> CatalogPrefixes { get; set; } = [];
    public List<PieceRow> Pieces { get; set; } = [];
}
