namespace CDArchive.Core.Data;

/// <summary>
/// Row entity for the <c>albums</c> table.
/// Identity: (<see cref="Label"/>, <see cref="CatalogueNumber"/>) unique when both set.
/// </summary>
public class AlbumRow
{
    public long Id { get; set; }

    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? Label { get; set; }
    public string? CatalogueNumber { get; set; }
    public string? Barcode { get; set; }
    public string? SparsCode { get; set; }
    public bool? IsStereo { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Folder name (or absolute path) containing this album's audio files,
    /// consumed by IArchiveAudioLocator. null when the album has no archive
    /// folder convention and must rely on per-track override paths.
    /// </summary>
    public string? ArchiveFolder { get; set; }

    /// <summary>True until explicitly approved. Defaults true for new albums.</summary>
    public bool IsProvisional { get; set; } = true;

    /// <summary>
    /// MusicBrainz Release ID (36-char UUID), or null if this album has not
    /// been linked to a MusicBrainz release entry.
    /// </summary>
    public string? MusicBrainzReleaseId { get; set; }

    // ── Recording session fields (formerly the album_sessions table) ─────────
    // One session-worth of fields directly on the album. Per-track copies
    // live on AlbumTrackRow. Engineers + Producers are name lists serialized
    // as JSON arrays, matching the pre-refactor shape.

    public string? SessionDates    { get; set; }
    public string? SessionVenue    { get; set; }
    public string? SessionCity     { get; set; }
    public string? SessionState    { get; set; }
    public string? SessionCountry  { get; set; }
    public string? SessionEngineersJson { get; set; }
    public string? SessionProducersJson { get; set; }

    public List<AlbumVolumeRow> Volumes { get; set; } = [];
    public List<AlbumDiscRow> Discs { get; set; } = [];
    public List<AlbumPerformerRow> Performers { get; set; } = [];
}
