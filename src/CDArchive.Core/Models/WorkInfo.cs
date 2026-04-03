namespace CDArchive.Core.Models;

public class WorkInfo
{
    public string Title { get; set; } = string.Empty;
    public string ComposerLastName { get; set; } = string.Empty;
    public List<MovementInfo> Movements { get; set; } = new();

    /// <summary>
    /// How many works of the same type (e.g. symphonies) this composer wrote.
    /// Used to determine whether work numbers need padding.
    /// Null means unknown.
    /// </summary>
    public int? CatalogueCountForType { get; set; }
}

public class MovementInfo
{
    public int Number { get; set; }
    public string Title { get; set; } = string.Empty;
}
