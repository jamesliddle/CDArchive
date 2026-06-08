namespace CDArchive.Core.Services;

/// <summary>
/// Result of <see cref="ItunesImportEnrichmentPlanner.PlanAsync"/>: the union
/// of MB suggestions for the album, composer, and piece dimensions of an
/// iTunes import batch.
///
/// <para>The review pane (slice 5) is the next consumer — it surfaces each
/// proposal as a row with the user's accept/reject choice. The apply phase
/// (slice 4) collapses the user's selections into an <c>EnrichmentChoices</c>
/// snapshot consumed by <see cref="ItunesImporter.Import"/>.</para>
///
/// <para>The lists are independent: each dimension is queried separately,
/// canon-skip rules differ, and the user can apply enrichment to one
/// dimension without the others.</para>
/// </summary>
public sealed record EnrichmentPlan(
    IReadOnlyList<AlbumEnrichmentProposal>  Albums,
    IReadOnlyList<ArtistEnrichmentProposal> Artists,
    IReadOnlyList<WorkEnrichmentProposal>   Works,
    IReadOnlyList<EnrichmentWarning>        Warnings)
{
    public static EnrichmentPlan Empty { get; } = new(
        Array.Empty<AlbumEnrichmentProposal>(),
        Array.Empty<ArtistEnrichmentProposal>(),
        Array.Empty<WorkEnrichmentProposal>(),
        Array.Empty<EnrichmentWarning>());
}

/// <summary>
/// MB suggestions for one iTunes album group (one <c>(Album, AlbumArtist)</c>
/// tuple, after the planner's <see cref="ItunesImportEnrichmentPlanner.BuildAlbumKey"/>
/// normalisation).
///
/// <para><see cref="Candidates"/> is empty when MB returned no hits or when
/// the album group was skipped by the planner (e.g. an existing approved
/// canon album already covers it — surfaced as an <see cref="EnrichmentWarning"/>
/// rather than an empty proposal).</para>
/// </summary>
public sealed record AlbumEnrichmentProposal(
    /// <summary>The planner's normalised album-group key (trimmed-lowered
    /// title + artist). The apply phase keys back into it to resolve the
    /// user's per-row choice to the matching importer album build.</summary>
    string ItunesAlbumKey,
    string ItunesAlbumTitle,
    string ItunesAlbumArtist,
    /// <summary>How many iTunes tracks landed in this album group — drives
    /// the review pane's "12 tracks" badge and the planner's track-count
    /// match boost in the underlying enricher search.</summary>
    int    TrackCount,
    IReadOnlyList<MbReleaseCandidate> Candidates,
    /// <summary>Index into <see cref="Candidates"/> of the top-ranked
    /// match (highest <c>Confidence</c>), or -1 when the list is empty.
    /// The review pane pre-selects this entry; user can change.</summary>
    int    PreferredIndex,
    /// <summary>The iTunes tracks that landed in this album group. The
    /// review pane renders them in the preview pane alongside MB's track
    /// listing so the user can verify the album matches the import
    /// selection track-by-track.</summary>
    IReadOnlyList<Models.ItunesTrack> ItunesTracks);

/// <summary>
/// MB suggestions for one parsed composer name. The planner skips composers
/// already fully populated in canon (approved AND with BirthYear set), so
/// every proposal here represents a missing-data fill opportunity.
/// </summary>
public sealed record ArtistEnrichmentProposal(
    string ParsedComposerName,
    IReadOnlyList<MbArtistSuggestion> Candidates,
    int    PreferredIndex);

/// <summary>
/// MB suggestions for one parsed <c>(composer, piece-title)</c> the importer
/// would create new. Pieces that already resolve via the canon's existing
/// reference index (per <see cref="ItunesImporter.ResolvesWithoutCreating"/>)
/// are skipped entirely.
///
/// <para><see cref="MovementCountMismatch"/> protects against the BWV 988
/// 32-vs-30 silent overwrite: when MB's top candidate's movement count
/// differs from the number of iTunes tracks the importer would map into
/// this piece, the flag is true and the review pane should auto-uncheck
/// the "Apply movement list" checkbox + surface a warning.</para>
/// </summary>
public sealed record WorkEnrichmentProposal(
    string ParsedComposerName,
    string ParsedPieceTitle,
    /// <summary>Count of iTunes tracks the importer would land under this
    /// piece, used as the comparison base for
    /// <see cref="MovementCountMismatch"/>.</summary>
    int    ExpectedMovementCount,
    IReadOnlyList<MbWorkSuggestion> Candidates,
    int    PreferredIndex,
    bool   MovementCountMismatch);

/// <summary>
/// Lightweight diagnostic surfaced to the user via the review pane.
/// <see cref="Category"/> is a stable, programmatic identifier
/// ("MovementCountMismatch" / "AmbiguousReleaseMatch" / etc.) so the UI
/// can style rows distinctly; <see cref="Message"/> is the
/// human-readable copy.
/// </summary>
public sealed record EnrichmentWarning(string Category, string Message);

/// <summary>
/// Progress reported by <see cref="ItunesImportEnrichmentPlanner.PlanAsync"/>
/// via the optional <see cref="IProgress{T}"/> the caller supplies. Drives
/// the review pane's live "Found 7 of 12 albums…" band so the user can hit
/// Apply mid-progress with whatever's resolved so far.
/// </summary>
public sealed record EnrichmentProgress(
    int AlbumsResolved,  int AlbumsTotal,
    int ArtistsResolved, int ArtistsTotal,
    int WorksResolved,   int WorksTotal);
