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
    /// Folder containing this album's audio files, used by the music player's
    /// path locator. Two shapes are accepted:
    ///   • A bare folder name (e.g. "Beethoven Symphonies 1 3 Bernstein") —
    ///     resolved against the configured archive root.
    ///   • An absolute path — used as-is (escape hatch for albums living
    ///     outside the configured root).
    /// null means the convention isn't used for this album; per-track
    /// <see cref="AlbumTrack.FlacPath"/> / <see cref="AlbumTrack.Mp3Path"/>
    /// overrides are the only way to locate its files.
    /// </summary>
    [JsonPropertyName("archive_folder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArchiveFolder { get; set; }

    /// <summary>
    /// True until the album is explicitly approved. New albums imported from iTunes
    /// start provisional; the user opts them into the canon by approving.
    /// </summary>
    [JsonPropertyName("is_provisional")]
    public bool IsProvisional { get; set; } = true;

    /// <summary>
    /// MusicBrainz Release ID (36-char UUID). Set when this album was
    /// matched against — or accepted from — a MusicBrainz release suggestion
    /// during iTunes import. Used to short-circuit future MB lookups for
    /// this album; surfaced read-only in the album editor.
    /// </summary>
    [JsonPropertyName("musicbrainz_release_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MusicBrainzReleaseId { get; set; }

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

    // ── Recording session fields (formerly the Sessions list) ────────────────
    // Pre-refactor: an album owned a List<RecordingSession> and each track
    // referenced one by stable Id. The user wanted the simpler shape — one
    // session-worth of fields directly on the album; tracks carry their own
    // copy of the same fields, defaulted from the album on TrackEditor open.
    // The legacy List<RecordingSession> + AlbumTrack.SessionId model is gone;
    // the migration copies session[0]'s fields up to the album.

    /// <summary>Freeform recording date, e.g. "March 3–7, 1967", "c.1963".</summary>
    [JsonPropertyName("session_dates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionDates { get; set; }

    /// <summary>Recording venue or studio name.</summary>
    [JsonPropertyName("session_venue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionVenue { get; set; }

    [JsonPropertyName("session_city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionCity { get; set; }

    /// <summary>State / province — sits between City and Country.</summary>
    [JsonPropertyName("session_state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionState { get; set; }

    [JsonPropertyName("session_country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionCountry { get; set; }

    [JsonPropertyName("session_engineers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionEngineers { get; set; }

    [JsonPropertyName("session_producers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionProducers { get; set; }

    /// <summary>
    /// Back-compat-only: lets JSON snapshots that still carry the legacy
    /// <c>"sessions": [ ... ]</c> array deserialize without data loss. The
    /// setter copies session[0]'s fields into the new flat <c>Session*</c>
    /// properties (matching the user's "keep only the first session" choice
    /// on the model migration). Sessions 2..N in the JSON are dropped.
    /// Getter always returns null so we never write the legacy shape back
    /// — re-exporting the JSON normalises everyone to the new fields.
    /// </summary>
    [JsonPropertyName("sessions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LegacySessionEntry>? LegacySessions
    {
        get => null;
        set
        {
            if (value is null or { Count: 0 }) return;
            var first = value[0];
            // ??= so the new flat fields win when both shapes are present
            // in the same JSON (defensive — shouldn't happen in practice).
            SessionDates     ??= first.Dates;
            SessionVenue     ??= first.Venue;
            SessionCity      ??= first.City;
            SessionState     ??= first.State;
            SessionCountry   ??= first.Country;
            SessionEngineers ??= first.Engineers;
            SessionProducers ??= first.Producers;
        }
    }

    // ── Computed helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Stable identity key used for merge / deduplication. Builds a composite
    /// of <c>Label|CatalogueNumber|Title|Subtitle</c>, trimming and using empty
    /// strings for nulls. Albums with no Label or CatalogueNumber (the user's
    /// classical-music collection has many of these — Böhm Beethoven cycles,
    /// Bernstein Mahler, etc.) still get a stable key from Title+Subtitle so
    /// the data service's <c>SaveAlbumsAsync</c> matches them on save instead
    /// of inserting duplicates.
    /// <para>
    /// Returns null only when every component is empty — which would be a
    /// genuinely unidentified album that always inserts fresh.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string? IdentityKey
    {
        get
        {
            var label    = (Label           ?? "").Trim();
            var catalog  = (CatalogueNumber ?? "").Trim();
            var title    = (Title           ?? "").Trim();
            var subtitle = (Subtitle        ?? "").Trim();
            if (label.Length == 0 && catalog.Length == 0 && title.Length == 0 && subtitle.Length == 0)
                return null;
            return $"{label}|{catalog}|{title}|{subtitle}";
        }
    }

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
/// Back-compat-only shape used by <see cref="CanonAlbum.LegacySessions"/> to
/// migrate JSON snapshots that still carry the pre-refactor
/// <c>"sessions": [ { dates, venue, city, state, country, engineers,
/// producers } ]</c> array. Never written; only deserialized.
/// </summary>
public class LegacySessionEntry
{
    [JsonPropertyName("dates")]     public string? Dates     { get; set; }
    [JsonPropertyName("venue")]     public string? Venue     { get; set; }
    [JsonPropertyName("city")]      public string? City      { get; set; }
    [JsonPropertyName("state")]     public string? State     { get; set; }
    [JsonPropertyName("country")]   public string? Country   { get; set; }
    [JsonPropertyName("engineers")] public List<string>? Engineers { get; set; }
    [JsonPropertyName("producers")] public List<string>? Producers { get; set; }
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

    /// <summary>
    /// On-disk folder name for this disc, used by the music player's path
    /// locator. null means the locator uses the default "Disc {DiscNumber}"
    /// (or no disc folder at all for single-disc albums). Set this for
    /// non-standard layouts like "Disc 3-06" in box sets.
    /// </summary>
    [JsonPropertyName("folder_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FolderName { get; set; }

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
    /// True until the track is explicitly approved. New tracks imported from iTunes
    /// start provisional; can be approved individually or as part of bulk album approval.
    /// </summary>
    [JsonPropertyName("is_provisional")]
    public bool IsProvisional { get; set; } = true;

    // ── Per-track recording session fields ────────────────────────────────────
    // Same shape as the album-level session fields on CanonAlbum. The
    // TrackEditor eagerly copies the album's values into blank fields on
    // open; the user can override any of them per track. Pre-refactor these
    // were a SessionId reference to one of the album's List<RecordingSession>
    // entries — see CanonAlbum's note on the model change.

    [JsonPropertyName("session_dates")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionDates { get; set; }

    [JsonPropertyName("session_venue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionVenue { get; set; }

    [JsonPropertyName("session_city")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionCity { get; set; }

    [JsonPropertyName("session_state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionState { get; set; }

    [JsonPropertyName("session_country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionCountry { get; set; }

    [JsonPropertyName("session_engineers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionEngineers { get; set; }

    [JsonPropertyName("session_producers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? SessionProducers { get; set; }

    /// <summary>
    /// Track-level SPARS code override (e.g. "DDD", "ADD").
    /// Overrides the album-level <see cref="CanonAlbum.SparsCode"/> for this track when set.
    /// null means "inherit album SPARS code".
    /// </summary>
    [JsonPropertyName("spars_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SparsCode { get; set; }

    /// <summary>
    /// Track-level stereo override. null = inherit album stereo; true = stereo; false = mono.
    /// </summary>
    [JsonPropertyName("stereo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsStereo { get; set; }

    /// <summary>
    /// Track-level performer override.  When non-null, replaces the album-level
    /// <see cref="CanonAlbum.Performers"/> list entirely for this track.
    /// null means "inherit album performers".
    /// </summary>
    [JsonPropertyName("performers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AlbumPerformer>? Performers { get; set; }

    /// <summary>
    /// Absolute path to this track's FLAC file when it can't be derived from
    /// the album/disc convention. null = let the locator derive it. Used for
    /// outliers like loose MP3s in the user's Music folder.
    /// </summary>
    [JsonPropertyName("flac_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FlacPath { get; set; }

    /// <summary>
    /// Absolute path to this track's MP3 file when it can't be derived from
    /// the album/disc convention. null = let the locator derive it.
    /// </summary>
    [JsonPropertyName("mp3_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mp3Path { get; set; }

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

    /// <summary>
    /// Like <see cref="DisplaySummary"/> but drops the "Composer – " prefix
    /// from each piece ref — for callers that surface the composer separately
    /// (e.g. the player caption title line).
    /// </summary>
    [JsonIgnore]
    public string DisplaySummaryWithoutComposer =>
        IsCatalogued
            ? string.Join(" / ", PieceRefs!.Select(r => r.DisplaySummaryWithoutComposer))
            : Description ?? "(no description)";
}
