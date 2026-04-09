using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace CDArchive.Core.Data;

/// <summary>
/// EF Core entity for a key-value application setting.
/// Used to store pick lists and other configuration blobs as JSON under a named key.
/// </summary>
[Table("Settings")]
public class SettingRow
{
    [Key]
    public string Key { get; set; } = "";

    public string? Value { get; set; }
}
