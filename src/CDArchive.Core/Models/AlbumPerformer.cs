using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// A performer (person or ensemble) credited on an album or a specific track.
/// Defined at album level by default; a track can carry its own list to override.
/// </summary>
public class AlbumPerformer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Role from the PerformerRoles pick list, e.g. "Piano", "Conductor", "Orchestra".
    /// </summary>
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>
    /// Optional freeform instrument detail when the role pick list isn't specific enough
    /// (e.g. "fortepiano", "natural horn", "theorbo").
    /// </summary>
    [JsonPropertyName("instrument")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instrument { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Display string, e.g. "Argerich, Martha – Piano" or "Berliner Philharmoniker".
    /// </summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            var detail = Instrument ?? Role;
            return string.IsNullOrWhiteSpace(detail) ? Name : $"{Name} – {detail}";
        }
    }
}
