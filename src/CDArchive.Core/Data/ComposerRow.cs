using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CDArchive.Core.Data;

/// <summary>
/// EF Core entity for a Canon composer.
/// All fields are flat strings matching the CanonComposer JSON schema.
/// </summary>
[Table("Composers")]
public class ComposerRow
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

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
}
