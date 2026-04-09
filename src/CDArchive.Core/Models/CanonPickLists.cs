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

    [JsonPropertyName("creative_roles")]
    public List<string> CreativeRoles { get; set; } = [];
}
