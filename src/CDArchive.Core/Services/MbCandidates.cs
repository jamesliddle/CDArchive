namespace CDArchive.Core.Services;

/// <summary>
/// Result records for the import-shaped MusicBrainz query surface
/// (<see cref="IMusicBrainzImportEnricher"/>). Slice 1 of the MB integration
/// only declares the shapes; the enricher impl + the planner + the apply
/// path land in subsequent slices.
/// <para>
/// These records are deliberately structural — they carry enough fields to
/// populate <see cref="Models.CanonAlbum"/>, <see cref="Models.CanonComposer"/>,
/// and <see cref="Models.CanonPiece"/> scalars without speculatively pulling
/// fields that the canon doesn't model. Each top-level record carries its
/// own MBID for persistence on the matching canon row (see
/// <c>musicbrainz_release_id</c>, <c>musicbrainz_artist_id</c>,
/// <c>musicbrainz_work_id</c>).
/// </para>
/// </summary>
public sealed record MbReleaseCandidate(
    string MbReleaseId,
    string? Title,
    string? Label,
    string? CatalogueNumber,
    string? Barcode,
    string? Date,
    string? Country,
    int    DiscCount,
    int    TrackCount,
    IReadOnlyList<MbReleaseTrack>   Tracks,
    IReadOnlyList<MbReleaseEvent>   RecordingEvents,
    IReadOnlyList<MbReleaseCredit>  Credits,
    double Confidence,
    /// <summary>
    /// Display string for MB's release artist-credit — joined "Name + Joinphrase + Name"
    /// chain (e.g. "Ludwig van Beethoven; Hélène Grimaud"). Populated for both
    /// search hits and detail fetches so the review pane can render it directly
    /// in the candidate dropdown without a per-row detail fetch.
    /// </summary>
    string? ArtistCredit = null);

/// <summary>
/// One track on an MB release, used to match against an iTunes
/// album-group's tracks at planner time (track-count + length-sum).
/// </summary>
public sealed record MbReleaseTrack(
    int       DiscNumber,
    int       TrackNumber,
    string    Title,
    TimeSpan? Length,
    string?   WorkMbid,
    string?   RecordingMbid);

/// <summary>
/// One recording-session event derived from MB release / recording
/// place-rels and artist-rels (engineer, producer). Maps onto the album's
/// flat session fields.
/// </summary>
public sealed record MbReleaseEvent(
    string?              Date,
    string?              Venue,
    string?              City,
    string?              Country,
    IReadOnlyList<string> Engineers,
    IReadOnlyList<string> Producers);

/// <summary>
/// One performer / contributor credit on an MB release. Sourced from
/// either <c>artist-credit</c> entries (free-text + artist refs) or
/// <c>artist-rels</c> (typed relations such as conductor, orchestra).
/// </summary>
public sealed record MbReleaseCredit(
    string  Name,
    string  Role,
    string? Instrument,
    string? Mbid);

/// <summary>
/// MB artist suggestion for the composer pass. Filling birth/death years
/// and place from MB rescues iTunes-XML rows that carry only "Beethoven"
/// without dates.
/// </summary>
public sealed record MbArtistSuggestion(
    string  MbArtistId,
    string  Name,
    string  SortName,
    int?    BirthYear,
    int?    DeathYear,
    string? BirthPlace,
    string? DeathPlace,
    double  Confidence);

/// <summary>
/// MB work suggestion for the piece pass. Carries canonical scalars
/// (title / catalogue / key) plus the movement enumeration that
/// (when applied) replaces the importer's parsed-from-iTunes subpiece
/// shell with MB's structure. The "Apply movement list" checkbox in the
/// review pane gates the latter.
/// </summary>
public sealed record MbWorkSuggestion(
    string  MbWorkId,
    string? Title,
    string? Catalogue,
    string? KeyTonality,
    string? KeyMode,
    IReadOnlyList<MbWorkMovement> Movements,
    double  Confidence);

/// <summary>
/// One movement within an MB work, sourced from the work's <c>parts</c>
/// relation chain. Number is 1-based and matches MB's ordering-key.
/// </summary>
public sealed record MbWorkMovement(
    int     Number,
    string  Title,
    string? Tempo);
