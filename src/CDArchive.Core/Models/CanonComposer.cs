using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// A composer entry in the Classical Canon reference data.
/// </summary>
public class CanonComposer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sort_name")]
    public string SortName { get; set; } = "";

    [JsonPropertyName("birth_date")]
    public string? BirthDate { get; set; }

    [JsonPropertyName("birth_local_place")]
    public string? BirthPlace { get; set; }

    [JsonPropertyName("birth_state")]
    public string? BirthState { get; set; }

    [JsonPropertyName("birth_country")]
    public string? BirthCountry { get; set; }

    [JsonPropertyName("birth_notes")]
    public string? BirthNotes { get; set; }

    [JsonPropertyName("death_date")]
    public string? DeathDate { get; set; }

    [JsonPropertyName("death_local_place")]
    public string? DeathPlace { get; set; }

    [JsonPropertyName("death_state")]
    public string? DeathState { get; set; }

    [JsonPropertyName("death_country")]
    public string? DeathCountry { get; set; }

    /// <summary>
    /// Extracts just the year from a date string like "1803-07-24" or returns the birth_notes.
    /// </summary>
    [JsonIgnore]
    public string BirthYear =>
        BirthDate != null && BirthDate.Length >= 4 ? BirthDate[..4]
        : BirthNotes ?? "";

    [JsonIgnore]
    public string DeathYear =>
        DeathDate != null && DeathDate.Length >= 4 ? DeathDate[..4] : "";

    [JsonIgnore]
    public string LifeSpan
    {
        get
        {
            var b = BirthYear;
            var d = DeathYear;
            if (string.IsNullOrEmpty(b) && string.IsNullOrEmpty(d))
                return "";
            if (string.IsNullOrEmpty(d))
                return $"(b. {b})";
            return $"({b}\u2013{d})";
        }
    }

    [JsonIgnore]
    public string BirthLocation
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(BirthPlace)) parts.Add(BirthPlace);
            if (!string.IsNullOrEmpty(BirthState)) parts.Add(BirthState);
            if (!string.IsNullOrEmpty(BirthCountry)) parts.Add(BirthCountry);
            return string.Join(", ", parts);
        }
    }

    [JsonIgnore]
    public string DeathLocation
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(DeathPlace)) parts.Add(DeathPlace);
            if (!string.IsNullOrEmpty(DeathState)) parts.Add(DeathState);
            if (!string.IsNullOrEmpty(DeathCountry)) parts.Add(DeathCountry);
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Numeric birth year for sorting. Returns int.MaxValue if unknown.
    /// </summary>
    [JsonIgnore]
    public int BirthYearSort =>
        BirthDate != null && BirthDate.Length >= 4 && int.TryParse(BirthDate[..4], out var y) ? y : int.MaxValue;

    /// <summary>
    /// Numeric death year for sorting. Returns int.MaxValue if unknown.
    /// </summary>
    [JsonIgnore]
    public int DeathYearSort =>
        DeathDate != null && DeathDate.Length >= 4 && int.TryParse(DeathDate[..4], out var y) ? y : int.MaxValue;

    /// <summary>
    /// Number of pieces for this composer. Set at runtime by the view model.
    /// </summary>
    [JsonIgnore]
    public int PieceCount { get; set; }

    public override string ToString() => $"{Name} {LifeSpan}";
}
