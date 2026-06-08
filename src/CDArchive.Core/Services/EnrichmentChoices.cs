namespace CDArchive.Core.Services;

/// <summary>
/// Frozen snapshot of the user's review-pane decisions, threaded through
/// <see cref="ItunesImporter.Import"/> as the apply-phase input. Each
/// dictionary is keyed the same way the planner keyed its proposals so
/// the importer can look up the right choice at create-time without
/// re-resolving.
///
/// <para>None of the apply rules silently overwrite existing canon data:</para>
/// <list type="bullet">
///   <item>Composer enrichment applies only when creating a new composer
///         OR when an existing canon composer is provisional with no
///         <see cref="Models.CanonComposer.BirthDate"/>.</item>
///   <item>Album enrichment applies only when creating a new album OR
///         when merging into a provisional canon album.</item>
///   <item>Piece enrichment applies only when creating a new piece. The
///         "Apply movement list" branch additionally requires the user
///         to have ticked the per-row checkbox (it's defaulted on when
///         MB's movement count matches the iTunes track count, off
///         otherwise).</item>
/// </list>
///
/// <para>Backwards compatibility: <see cref="ItunesImporter.Import"/>'s
/// <c>enrichment</c> parameter defaults to null. The legacy non-MB
/// import path is unchanged when null is passed.</para>
/// </summary>
public sealed record EnrichmentChoices(
    IReadOnlyDictionary<string, AppliedAlbumEnrichment>  AlbumsByKey,
    IReadOnlyDictionary<string, AppliedArtistEnrichment> ArtistsByName,
    IReadOnlyDictionary<string, AppliedWorkEnrichment>   WorksByKey)
{
    public static EnrichmentChoices Empty { get; } = new(
        new Dictionary<string, AppliedAlbumEnrichment>(StringComparer.Ordinal),
        new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Canonical key for a work lookup. Matches the planner's grouping
    /// (lowercased composer + title) so a planner proposal and an applied
    /// choice round-trip cleanly.
    /// </summary>
    public static string BuildWorkKey(string composer, string title) =>
        $"{composer}|{title}".ToLowerInvariant();
}

/// <summary>
/// One album row in <see cref="EnrichmentChoices.AlbumsByKey"/>. The three
/// per-aspect Apply flags match the review pane's per-row checkboxes
/// (Metadata / Performers / Recording session) so the user can opt into
/// any subset.
/// </summary>
public sealed record AppliedAlbumEnrichment(
    MbReleaseCandidate Candidate,
    bool ApplyMetadata        = true,
    bool ApplyPerformers      = true,
    bool ApplyRecordingSession = true);

/// <summary>
/// One artist row. There's a single "Apply" checkbox so the row record is
/// minimal; the candidate carries the full MB suggestion.
/// </summary>
public sealed record AppliedArtistEnrichment(
    MbArtistSuggestion Candidate,
    bool Apply = true);

/// <summary>
/// One work row. Two per-aspect Apply flags — scalar fields (title /
/// catalogue / key) and the movement list. The review pane defaults
/// ApplyMovementList to false when the planner detected a movement-count
/// mismatch.
/// </summary>
public sealed record AppliedWorkEnrichment(
    MbWorkSuggestion Candidate,
    bool ApplyScalars      = true,
    bool ApplyMovementList = true);
