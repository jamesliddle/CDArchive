namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>composer_aliases</c>. Ordered list of alternative names a composer is known by.
/// </summary>
public class ComposerAliasRow
{
    public long Id { get; set; }

    public long ComposerId { get; set; }
    public ComposerRow Composer { get; set; } = null!;

    public int Position { get; set; }
    public string Alias { get; set; } = "";
}
