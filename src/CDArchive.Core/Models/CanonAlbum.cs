using System.Text.Json.Serialization;

namespace CDArchive.Core.Models;

/// <summary>
/// A catalogued physical CD (or multi-disc set) in the collection.
/// Identity key: <see cref="Label"/> + <see cref="CatalogueNumber"/>.
/// </summary>
public class CanonAlbum
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; set; }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>Primary identity key component (e.g. "476 1276", "BRL 99362").</summary>
    [JsonPropertyName("catalogue_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CatalogueNumber { get; set; }

    [JsonPropertyName("barcode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Barcode { get; set; }

    /// <summary>SPARS code, e.g. "DDD", "ADD".</summary>
    [JsonPropertyName("spars_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SparsCode { get; set; }

    /// <summary>null = unknown; true = stereo; false = mono.</summary>
    [JsonPropertyName("stereo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsStereo { get; set; }

    [JsonPropertyName("notes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Notes { get; set; }

    /// <summary>
    /// Optional volume grouping for large box sets (e.g. the Brilliant Classics Bach Edition).
    /// null for single-disc albums and ordinary multi-disc sets.
    /// Each <see cref="AlbumDisc"/> references its volume via <see cref="AlbumDisc.VolumeNumber"/>.
    /// </summary>
    [JsonPropertyName("volumes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlbumVolume>? Volumes { get; set; }

    [JsonPropertyName("discs")]
    public List<AlbumDisc> Discs { get; set; } = [];

    /// <summary>
    /// Album-level performers.  Applies to all tracks unless a track carries its own
    /// <see cref="AlbumTrack.Performers"/> override (non-null overrides the whole list).
    /// </summary>
    [JsonPropertyName("performers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlbumPerformer>? Performers { get; set; }

    /// <summary>
    /// Recording sessions.  Most albums have one entry that covers all tracks.
    /// Tracks reference a session by index via <see cref="AlbumTrack.SessionIndex"/>.
    /// </summary>
    [JsonPropertyName("sessions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RecordingSession>? Sessions { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Stable identity key used for merge/deduplication: "Label|CatalogueNumber".
    /// Returns null if either component is missing.
    /// </summary>
    [JsonIgnore]
    public string? IdentityKey =>
        !string.IsNullOrWhiteSpace(Label) && !string.IsNullOrWhiteSpace(CatalogueNumber)
            ? $"{Label.Trim()}|{CatalogueNumber.Trim()}"
            : null;

    /// <summary>Short title for list views.</summary>
    [JsonIgnore]
    public string DisplayTitle => Title ?? CatalogueNumber ?? "(untitled)";

    /// <summary>Total number of discs across all volumes.</summary>
    [JsonIgnore]
    public int DiscCount => Discs.Count;

    /// <summary>Total number of tracks across all discs.</summary>
    [JsonIgnore]
    public int TotalTrackCount => Discs.Sum(d => d.Tracks.Count);

    /// <summary>
    /// Short performer summary for list display: first performer's name + role,
    /// with a count of additional performers if there are more than one.
    /// </summary>
    [JsonIgnore]
    public string PerformerSummary
    {
        get
        {
            if (Performers is null or { Count: 0 }) return "";
            var first = Performers[0].DisplayName;
            return Performers.Count == 1 ? first : $"{first} +{Performers.Count - 1} more";
        }
    }
}

/// <summary>
/// An optional grouping level between a box set and its individual discs,
/// for sets organised into named volumes (e.g. "Volume 3: Wind Music").
/// </summary>
public class AlbumVolume
{
    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("subtitle")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subtitle { get; set; }
}

/// <summary>
/// One physical disc.  Disc numbers restart at 1 within each volume (or within
/// the album if there are no volumes).
/// </summary>
public class AlbumDisc
{
    [JsonPropertyName("disc_number")]
    public int DiscNumber { get; set; }

    /// <summary>null if the album has no volume grouping.</summary>
    [JsonPropertyName("volume_number")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? VolumeNumber { get; set; }

    /// <summary>Optional disc sub-title (e.g. "Keyboard Works, Vol. 1").</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("tracks")]
    public List<AlbumTrack> Tracks { get; set; } = [];
}

/// <summary>
/// One track on a disc.
/// A track is either <em>catalogued</em> (has one or more <see cref="PieceRefs"/>) or
/// <em>uncatalogued</em> (<see cref="PieceRefs"/> is null or empty, and <see cref="Description"/>
/// is used instead — e.g. "Interview with the pianist").
/// </summary>
public class AlbumTrack
{
    [JsonPropertyName("track_number")]
    public int TrackNumber { get; set; }

    /// <summary>Track duration in "m:ss" or "h:mm:ss" format.</summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }

    /// <summary>
    /// Freeform label used when this track is not linked to any canon piece
    /// (e.g. "Interview with Brendel", "Applause").
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// One or more links into the Canon.  Empty / null means the track is uncatalogued;
    /// use <see cref="Description"/> for display.
    /// </summary>
    [JsonPropertyName("piece_refs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TrackPieceRef>? PieceRefs { get; set; }

    /// <summary>
    /// Zero-based index into <see cref="CanonAlbum.Sessions"/>.
    /// null means either the album has a single session (index 0 implied) or session
    /// information is unknown.
    /// </summary>
    [JsonPropertyName("session_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SessionIndex { get; set; }

    /// <summary>
    /// Track-level SPARS code override (e.g. "DDD", "ADD").
    /// Overrides the album-level <see cref="CanonAlbum.SparsCode"/> for this track when set.
    /// null means "inherit album SPARS code".
    /// </summary>
    [JsonPropertyName("spars_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SparsCode { get; set; }

    /// <summary>
    /// Track-level performer override.  When non-null, replaces the album-level
    /// <see cref="CanonAlbum.Performers"/> list entirely for this track.
    /// null means "inherit album performers".
    /// </summary>
    [JsonPropertyName("performers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlbumPerformer>? Performers { get; set; }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>True if the track has at least one canon piece reference.</summary>
    [JsonIgnore]
    public bool IsCatalogued => PieceRefs is { Count: > 0 };

    /// <summary>Single-line summary for list display.</summary>
    [JsonIgnore]
    public string DisplaySummary =>
        IsCatalogued
            ? string.Join(" / ", PieceRefs!.Select(r => r.DisplaySummary))
            : Description ?? "(no description)";
}
