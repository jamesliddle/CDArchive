namespace CDArchive.Core.Services;

/// <summary>
/// Import-shaped MusicBrainz query surface, separate from
/// <see cref="ICatalogueReference"/> (which is tagging-shaped).
///
/// <para>
/// Implementations compose <see cref="MusicBrainzReference"/>'s rate-limited
/// GET — they don't open their own rate-limit gate, so the cataloguing
/// flow and the import flow share one process-wide gate. Per-session
/// result caching (keyed on the search arguments) is the implementation's
/// responsibility; a re-import or retry should not re-query MB.
/// </para>
///
/// <para>
/// All methods are cancellation-aware. The planner runs concurrently
/// from the caller's POV (release / artist / work passes interleave) but
/// every outbound request serialises through the MB rate-limit gate.
/// </para>
///
/// <para>
/// Slice 1 declares the interface only; the implementation, planner,
/// review-pane UI, and importer apply path land in later slices.
/// </para>
/// </summary>
public interface IMusicBrainzImportEnricher
{
    /// <summary>
    /// Search MB releases by title + artist. <paramref name="trackLengths"/>
    /// (in iTunes-source order) lets the impl rank candidates by length-sum
    /// proximity in addition to MB's own <c>score</c>; pass an empty list
    /// when length info isn't available. Up to <paramref name="limit"/>
    /// candidates returned, ordered by descending confidence.
    /// </summary>
    Task<IReadOnlyList<MbReleaseCandidate>> SearchReleasesAsync(
        string albumTitle,
        string albumArtist,
        IReadOnlyList<TimeSpan> trackLengths,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// MBID shortcut path: fetch a single MB release by its MBID. Used when
    /// iTunes carries MB tags (typically populated by MusicBrainz Picard)
    /// so the planner can skip the search step entirely. Returns null on
    /// 404 or transient failure (the impl handles MB's retry policy).
    /// </summary>
    Task<MbReleaseCandidate?> GetReleaseByMbidAsync(
        string mbReleaseId,
        CancellationToken ct);

    /// <summary>
    /// Resolve a composer by surname (+ optional given name) into MB artist
    /// suggestions. Used by the planner's artist pass to fill blank dates +
    /// sort name on iTunes-imported composers. Top candidates returned,
    /// ordered by descending confidence; the planner picks the top by
    /// default but the user can override via the review pane.
    /// </summary>
    Task<IReadOnlyList<MbArtistSuggestion>> ResolveArtistAsync(
        string lastName,
        string? firstName,
        CancellationToken ct);

    /// <summary>
    /// Resolve a piece by composer + parsed-piece-title into MB work
    /// suggestions. The impl walks <c>work-rels</c> to populate the
    /// <see cref="MbWorkSuggestion.Movements"/> projection for the
    /// optional "Apply movement list" review-pane checkbox.
    /// </summary>
    Task<IReadOnlyList<MbWorkSuggestion>> ResolveWorkAsync(
        string composerName,
        string parsedPieceTitle,
        CancellationToken ct);
}
