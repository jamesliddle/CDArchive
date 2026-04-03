namespace CDArchive.Core.Models;

public class ComposerInfo
{
    public string LastName { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public int? BirthYear { get; set; }
    public int? DeathYear { get; set; }

    /// <summary>
    /// The formatted composer string: "Last, First (birth–death)".
    /// </summary>
    public string Formatted =>
        BirthYear.HasValue
            ? $"{LastName}, {FirstName} ({BirthYear}\u2013{DeathYear?.ToString() ?? ""})"
            : $"{LastName}, {FirstName}";
}
