using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// Predefined dropdown choices for the Canon piece editor.
/// </summary>
public class CanonPickLists
{
    [JsonPropertyName("forms")]
    public List<string> Forms { get; set; } = [];

    [JsonPropertyName("categories")]
    public List<string> Categories { get; set; } = [];

    [JsonPropertyName("catalog_prefixes")]
    public List<string> CatalogPrefixes { get; set; } = [];

    [JsonPropertyName("key_tonalities")]
    public List<string> KeyTonalities { get; set; } = [];

    [JsonPropertyName("voice_types")]
    public List<string> VoiceTypes { get; set; } = [];

    [JsonPropertyName("instruments")]
    public List<string> Instruments { get; set; } = [];

    [JsonPropertyName("ensembles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EnsembleDefinition>? Ensembles { get; set; }

    [JsonPropertyName("creative_roles")]
    public List<string> CreativeRoles { get; set; } = [];

    /// <summary>
    /// Roles for album performers, e.g. "Piano", "Conductor", "Orchestra".
    /// Shown in the album editor's performer list.
    /// </summary>
    [JsonPropertyName("performer_roles")]
    public List<string> PerformerRoles { get; set; } = [];

    /// <summary>
    /// Record-label names for the Album editor's Label dropdown.
    /// </summary>
    [JsonPropertyName("labels")]
    public List<string> Labels { get; set; } = [];
}
