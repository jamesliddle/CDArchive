namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>people</c> table. Performers and any other humans
/// referenced by albums. Composers are a separate entity (see <see cref="ComposerRow"/>).
/// </summary>
public class PersonRow
{
    public long Id { get; set; }

    public string Name { get; set; } = "";
    public string SortName { get; set; } = "";

    public string? BirthDate { get; set; }
    public string? DeathDate { get; set; }
    public string? Notes { get; set; }

    public List<EnsembleMembershipRow> Memberships { get; set; } = [];
    public List<AlbumPerformerRow> PerformerCredits { get; set; } = [];
}
