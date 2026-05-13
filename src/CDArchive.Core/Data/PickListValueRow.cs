namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for <c>pick_list_values</c>. Single table for all pick lists
/// (forms, categories, catalog_prefixes, key_tonalities, voice_types,
/// instruments, ensembles, creative_roles, performer_roles, labels).
/// Simple string lists store their value in <see cref="Value"/>; structured
/// lists (<c>ensembles</c> — <c>EnsembleDefinition</c>) use <see cref="ValueJson"/>.
/// </summary>
public class PickListValueRow
{
    public long Id { get; set; }

    public string ListName { get; set; } = "";
    public int Position { get; set; }

    public string? Value { get; set; }
    public string? ValueJson { get; set; }
}
