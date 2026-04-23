using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// One recording session contributing to an album.
/// Defined at album level; individual tracks reference a session by its zero-based
/// index in <see cref="CanonAlbum.Sessions"/> via <see cref="AlbumTrack.SessionIndex"/>.
/// Most albums have exactly one session (no track-level index needed).
/// </summary>
public class RecordingSession
{
    /// <summary>
    /// Freeform date string, e.g. "March 3–7, 1967", "1955", "c.1963", "June 1981".
    /// </summary>
    [JsonPropertyName("dates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dates { get; set; }

    /// <summary>Recording venue or studio name.</summary>
    [JsonPropertyName("venue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Venue { get; set; }

    [JsonPropertyName("city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? City { get; set; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; set; }

    [JsonPropertyName("engineers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Engineers { get; set; }

    [JsonPropertyName("producers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Producers { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>Single-line location summary for display.</summary>
    [JsonIgnore]
    public string LocationSummary
    {
        get
        {
            var parts = new[] { Venue, City, Country }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(", ", parts);
        }
    }

    /// <summary>Single-line summary for list display.</summary>
    [JsonIgnore]
    public string DisplaySummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Dates))    parts.Add(Dates!);
            var location = LocationSummary;
            if (!string.IsNullOrWhiteSpace(location)) parts.Add(location);
            return parts.Count > 0 ? string.Join(" · ", parts) : "(no session details)";
        }
    }
}
