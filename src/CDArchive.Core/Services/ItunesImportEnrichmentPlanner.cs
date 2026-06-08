using CDArchive.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

/// <summary>
/// Runs the three MB pre-flight passes (Releases / Artists / Works) for an
/// iTunes import batch, returning an <see cref="EnrichmentPlan"/> the review
/// pane (slice 5) can render.
///
/// <para>Sub-pass shape:</para>
/// <list type="bullet">
///   <item><b>Releases.</b> Group the input by <c>(Album, AlbumArtist)</c>;
///         for each group, search MB releases. Skip groups whose iTunes
///         album already matches an approved canon album.</item>
///   <item><b>Artists.</b> Dedup parsed composer names; skip composers that
///         are approved AND have BirthYear set (the canon already covers
///         them). Provisional composers and approved-with-blank-dates do
///         get proposals.</item>
///   <item><b>Works.</b> Dedup <c>(composer, parsed-piece-title)</c>; skip
///         pieces that already resolve against the canon via
///         <see cref="ItunesImporter.ResolvesWithoutCreating"/> (either
///         dot-separator interpretation). Detect movement-count mismatches
///         on the top candidate.</item>
/// </list>
///
/// <para>Every MB call serialises through the underlying
/// <see cref="MusicBrainzReference"/> rate-limit gate (via
/// <see cref="IMusicBrainzImportEnricher"/>). The planner enumerates
/// sequentially within each pass and starts the next pass only after the
/// previous one finishes — concurrency would gain nothing because the
/// gate is single-slot. Progress is reported per-resolved item via the
/// optional <see cref="IProgress{T}"/>.</para>
///
/// <para>Cancellation: every enricher call passes through the supplied
/// <see cref="CancellationToken"/>. A cancel mid-pass returns the partial
/// plan (with whatever resolved so far + any warnings already emitted) so
/// the user can Apply with the partial set if they want.</para>
/// </summary>
public sealed class ItunesImportEnrichmentPlanner
{
    public const int DefaultCandidatesPerProposal = 3;

    private readonly IMusicBrainzImportEnricher _mb;
    private readonly ILogger<ItunesImportEnrichmentPlanner> _logger;

    public ItunesImportEnrichmentPlanner(
        IMusicBrainzImportEnricher mb,
        ILogger<ItunesImportEnrichmentPlanner>? logger = null)
    {
        _mb = mb;
        _logger = logger ?? NullLogger<ItunesImportEnrichmentPlanner>.Instance;
    }

    /// <summary>
    /// Build the enrichment plan for the given iTunes import batch.
    /// </summary>
    /// <param name="tracks">Selected iTunes tracks (the same set that
    ///   would be passed to <see cref="ItunesImporter.Import"/>).</param>
    /// <param name="existingComposers">Current canon composers — drives
    ///   the artist-pass skip rule.</param>
    /// <param name="existingPieces">Current canon pieces — drives the
    ///   work-pass skip rule via
    ///   <see cref="ItunesImporter.ResolvesWithoutCreating"/>.</param>
    /// <param name="existingAlbums">Current canon albums — drives the
    ///   album-pass skip rule for already-imported approved albums.</param>
    /// <param name="dotInterpretations">Per-track dot-separator choices
    ///   from the existing pre-resolve pass. The planner consults BOTH
    ///   interpretations when deciding whether a work already resolves
    ///   (i.e. proposes only when neither interpretation lands on
    ///   existing canon).</param>
    /// <param name="progress">Optional sink for live progress updates.</param>
    /// <param name="candidatesPerProposal">Soft cap on candidates surfaced
    ///   per proposal. The underlying enricher already caps at 5/25 for
    ///   MB; this is the final trim before the plan is returned. Default 3.</param>
    public async Task<EnrichmentPlan> PlanAsync(
        IReadOnlyList<ItunesTrack> tracks,
        IReadOnlyList<CanonComposer> existingComposers,
        IReadOnlyList<CanonPiece>    existingPieces,
        IReadOnlyList<CanonAlbum>    existingAlbums,
        IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation>? dotInterpretations,
        IProgress<EnrichmentProgress>? progress,
        int candidatesPerProposal = DefaultCandidatesPerProposal,
        CancellationToken ct = default)
    {
        if (tracks.Count == 0) return EnrichmentPlan.Empty;

        var warnings = new List<EnrichmentWarning>();

        // Build the throwaway resolver once for the work-pass canon-skip
        // check. registerAsCurrent:false matches the iTunes importer pattern
        // — we don't disturb the live PieceReferenceIndex singleton (H7).
        var preResolver = new PieceReferenceIndex(registerAsCurrent: false);
        preResolver.BuildResolver(existingPieces);

        // ── Pass 1: Releases ────────────────────────────────────────────────
        var albumGroups = GroupByAlbum(tracks);
        var approvedAlbumKeys = BuildApprovedAlbumKeySet(existingAlbums);

        var albumProposals = new List<AlbumEnrichmentProposal>();
        int albumsTotal = albumGroups.Count;
        int albumsResolved = 0;
        ReportProgress(progress, albumsResolved, albumsTotal, 0, 0, 0, 0);

        // Cancel-tolerant: instead of bubbling OperationCanceledException, we
        // break out and return whatever's resolved so far + a "Cancelled"
        // warning. The review pane lets the user Apply with the partial set
        // if they want; the importer treats the partial choices like any
        // other (per-row apply guards still hold).
        bool cancelled = false;
        foreach (var group in albumGroups)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            // Skip if an approved canon album already covers this group —
            // surfaced as a warning so the user knows MB lookup was skipped.
            if (approvedAlbumKeys.Contains(group.Key))
            {
                warnings.Add(new EnrichmentWarning(
                    "AlbumAlreadyApproved",
                    $"Skipped \"{group.Title}\" / \"{group.AlbumArtist}\" — already in canon."));
                albumsResolved++;
                ReportProgress(progress, albumsResolved, albumsTotal, 0, 0, 0, 0);
                continue;
            }

            var trackLengths = group.Tracks
                .Where(t => t.DurationMs is > 0)
                .Select(t => TimeSpan.FromMilliseconds(t.DurationMs!.Value))
                .ToList();

            // MBID shortcut: if any track in this album group carries a
            // MusicBrainz Release ID (Picard / similar pre-tagged), fetch
            // the release by MBID directly. Bypasses the search and is
            // unambiguous. All tracks in the same group should agree on
            // the release MBID; we use the first non-null we find.
            var groupMbReleaseId = group.Tracks
                .Select(t => t.MbReleaseId)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

            IReadOnlyList<MbReleaseCandidate> raw;
            try
            {
                if (!string.IsNullOrEmpty(groupMbReleaseId))
                {
                    var byId = await _mb.GetReleaseByMbidAsync(groupMbReleaseId, ct).ConfigureAwait(false);
                    raw = byId is not null
                        ? new[] { byId }
                        : Array.Empty<MbReleaseCandidate>();
                }
                else
                {
                    // Build the artist hint by combining two complementary signals:
                    //
                    //   • Performer hint: AlbumArtist when set; otherwise the
                    //     intersection of every track's Artist field
                    //     ("Boston Symphony Orchestra, Andris Nelsons" shared
                    //     across all tracks of a Brahms cycle).
                    //   • Composer hint: top-3 most-frequent composers across
                    //     the album's tracks. Reinforces title matches for
                    //     single-composer albums; surfaces the right release
                    //     for 2-3-composer compilations ("String Quartets:
                    //     Beethoven, Schubert, Brahms") that the performer
                    //     intersection misses.
                    //
                    // Both go to MB's artist: Lucene field via the enricher's
                    // tokeniser — MB indexes classical artist-credit with both
                    // performers AND composers, so both signals reinforce
                    // matches against the same field.
                    var performerHint = !string.IsNullOrWhiteSpace(group.AlbumArtist)
                        ? group.AlbumArtist!
                        : ComputeSharedPerformerHint(group);
                    var composerHint = ComputeComposerHint(group);

                    var artistHint = string.IsNullOrEmpty(composerHint)
                        ? performerHint
                        : string.IsNullOrEmpty(performerHint)
                            ? composerHint
                            : $"{composerHint} {performerHint}";

                    _logger.LogInformation(
                        "Planner: search hints for \"{Title}\" → artist=\"{Artist}\" "
                        + "(performers=\"{Performers}\", composers=\"{Composers}\")",
                        group.Title, artistHint, performerHint, composerHint);

                    raw = await _mb.SearchReleasesAsync(
                        group.Title,
                        artistHint,
                        trackLengths,
                        limit: candidatesPerProposal,
                        ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { cancelled = true; break; }

            var candidates = raw.Take(candidatesPerProposal).ToList();
            albumProposals.Add(new AlbumEnrichmentProposal(
                ItunesAlbumKey:    group.Key,
                ItunesAlbumTitle:  group.Title,
                ItunesAlbumArtist: group.AlbumArtist ?? string.Empty,
                TrackCount:        group.Tracks.Count,
                Candidates:        candidates,
                PreferredIndex:    candidates.Count > 0 ? 0 : -1,
                // Order the preview-pane track list by disc/track so it reads
                // in album order, regardless of the selection's source order
                // (the grid sorts albums newest-first, which can interleave
                // out-of-DateAdded-order tracks). Nulls last so an unnumbered
                // track lands at the end rather than the top.
                ItunesTracks:      group.Tracks
                    .OrderBy(t => t.DiscNumber ?? 1)
                    .ThenBy(t => t.TrackNumber ?? int.MaxValue)
                    .ToList()));

            _logger.LogInformation(
                "Planner: album proposal for \"{Title}\" / \"{Artist}\" stored {Count} candidate(s); "
                + "top: \"{TopTitle}\" — {TopCredit} (conf {Conf:P0}, MBID {Mbid})",
                group.Title, group.AlbumArtist ?? "",
                candidates.Count,
                candidates.Count > 0 ? candidates[0].Title : "(none)",
                candidates.Count > 0 ? candidates[0].ArtistCredit ?? "" : "",
                candidates.Count > 0 ? candidates[0].Confidence : 0.0,
                candidates.Count > 0 ? candidates[0].MbReleaseId : "");

            if (candidates.Count == 0)
            {
                warnings.Add(new EnrichmentWarning(
                    "NoReleaseMatch",
                    $"MusicBrainz found no release for \"{group.Title}\" / \"{group.AlbumArtist}\"."));
            }
            else if (candidates.Count > 1 && candidates[0].Confidence < 0.80)
            {
                warnings.Add(new EnrichmentWarning(
                    "AmbiguousReleaseMatch",
                    $"\"{group.Title}\" / \"{group.AlbumArtist}\" — top match confidence is low ({candidates[0].Confidence:P0}); review carefully."));
            }

            albumsResolved++;
            ReportProgress(progress, albumsResolved, albumsTotal, 0, 0, 0, 0);
        }

        // Artist + work passes intentionally skipped (user scoped MB
        // enrichment to album metadata only). The proposal record types
        // are kept for back-compat with EnrichmentPlan's shape; the
        // dictionaries on EnrichmentChoices route around them as
        // empty-no-op when the importer applies.
        var artistProposals = new List<ArtistEnrichmentProposal>();
        var workProposals = new List<WorkEnrichmentProposal>();

        if (cancelled)
        {
            warnings.Add(new EnrichmentWarning(
                "Cancelled",
                "Enrichment was cancelled. Only partial proposals are shown; you can still apply the resolved ones."));
        }

        return new EnrichmentPlan(albumProposals, artistProposals, workProposals, warnings);
    }

    // ── Album grouping ───────────────────────────────────────────────────────

    internal sealed record AlbumGroup(
        string Key, string Title, string? AlbumArtist, List<ItunesTrack> Tracks);

    /// <summary>
    /// Derive an artist hint for the MB release search from the intersection
    /// of every track's <see cref="ItunesTrack.Artist"/> field, comma-split
    /// the same way <c>ItunesImporter.ParsePerformers</c> does.
    ///
    /// <para>For a typical classical album every track lists the same
    /// performers (e.g. "Boston Symphony Orchestra, Andris Nelsons"), so the
    /// intersection equals the full performer list — and that's exactly
    /// what MB indexes as <c>artist-credit</c> on the release. For a
    /// compilation where tracks have different performers, the intersection
    /// is empty and we return an empty string so the caller searches by
    /// album title alone.</para>
    ///
    /// <para>Order in the result follows the first track's performer order,
    /// so a query like "Boston Symphony Orchestra Andris Nelsons" tokens
    /// from left to right and the artist-credit substring boost in the
    /// enricher picks up exact matches cleanly.</para>
    /// </summary>
    internal static string ComputeSharedPerformerHint(AlbumGroup group)
    {
        if (group.Tracks.Count == 0) return "";

        var perTrack = group.Tracks
            .Select(t => SplitArtistField(t.Artist))
            .ToList();
        if (perTrack[0].Count == 0) return "";

        // Intersect each subsequent track's performer set, preserving the
        // first track's order. Case-insensitive comparison absorbs minor
        // capitalisation drift across tracks.
        var shared = new List<string>(perTrack[0]);
        for (int i = 1; i < perTrack.Count; i++)
        {
            var keep = new HashSet<string>(perTrack[i], StringComparer.OrdinalIgnoreCase);
            shared = shared.Where(s => keep.Contains(s)).ToList();
            if (shared.Count == 0) return "";
        }
        return string.Join(' ', shared);
    }

    /// <summary>
    /// Comma-split an iTunes Artist field into individual performer names.
    /// Mirrors <c>ItunesImporter.ParsePerformers</c>'s contract so the
    /// planner's hint shape matches the importer's downstream behaviour.
    /// </summary>
    internal static List<string> SplitArtistField(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return new();
        return artist
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Composer hint for the MB release search. Parses each track's
    /// <see cref="ItunesTrack.Composer"/> field, counts occurrences
    /// case-insensitively, and returns the top-<paramref name="maxComposers"/>
    /// by frequency joined with spaces.
    ///
    /// <para>For typical single-composer albums this returns the composer
    /// verbatim. For multi-composer compilations (e.g. "String Quartets:
    /// Beethoven, Schubert, Brahms") the top contributors are all included.
    /// For very-diverse compilations the cap prevents query bloat — the
    /// top-3 most-frequent composers carry most of the matching signal
    /// and the long tail just dilutes Lucene scoring.</para>
    ///
    /// <para>MB's release artist-credit for classical typically credits
    /// the composer alongside the performers, so these tokens land in
    /// the same Lucene field as the performer hint and reinforce title
    /// matches.</para>
    /// </summary>
    internal static string ComputeComposerHint(AlbumGroup group, int maxComposers = 3)
    {
        if (group.Tracks.Count == 0) return "";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in group.Tracks)
        {
            var parsed = ItunesImportInference.ParseComposer(t.Composer);
            if (parsed is null) continue;
            // The "(Various)" sentinel covers multi-composer collaborative
            // pieces (e.g. L'éventail de Jeanne) — it's a wrapper, not a
            // searchable artist name. Skip so we don't add "Various" as a
            // useless query term.
            if (string.Equals(parsed.Name, "(Various)", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(parsed.Name, "Various", StringComparison.OrdinalIgnoreCase)) continue;
            counts[parsed.Name] = counts.GetValueOrDefault(parsed.Name) + 1;
        }

        if (counts.Count == 0) return "";

        var top = counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxComposers)
            .Select(kv => kv.Key)
            .ToList();

        return string.Join(' ', top);
    }

    /// <summary>
    /// Group selected tracks by their <c>(Album, AlbumArtist)</c> key. Tracks
    /// with no Album field are intentionally excluded — they become loose
    /// tracks in the importer and don't get release enrichment (a release
    /// match implies the user wants album-level metadata, which loose
    /// tracks don't have a slot for).
    /// </summary>
    internal static List<AlbumGroup> GroupByAlbum(IReadOnlyList<ItunesTrack> tracks)
    {
        var groups = new Dictionary<string, AlbumGroup>(StringComparer.Ordinal);
        foreach (var t in tracks)
        {
            if (string.IsNullOrWhiteSpace(t.Album)) continue;
            var key = BuildAlbumKey(t.Album, t.AlbumArtist);
            if (!groups.TryGetValue(key, out var g))
            {
                g = new AlbumGroup(key, t.Album.Trim(), t.AlbumArtist?.Trim(), new List<ItunesTrack>());
                groups[key] = g;
            }
            g.Tracks.Add(t);
        }
        return groups.Values.ToList();
    }

    /// <summary>
    /// Normalised dedup key — trimmed-lowered <c>title|artist</c>. Same shape
    /// as the H24 retirement's iTunes-side filter so this lookup agrees with
    /// the importer's "already imported" check.
    /// </summary>
    internal static string BuildAlbumKey(string title, string? albumArtist)
    {
        var t = (title ?? "").Trim().ToLowerInvariant();
        var a = (albumArtist ?? "").Trim().ToLowerInvariant();
        return $"{t}|{a}";
    }

    private static HashSet<string> BuildApprovedAlbumKeySet(IReadOnlyList<CanonAlbum> existing)
    {
        // Approved-only — provisional canon albums are still candidates for
        // enrichment. The user opted those in via approval; before that, MB
        // metadata is fair game.
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in existing)
        {
            if (a.IsProvisional) continue;
            if (string.IsNullOrWhiteSpace(a.Title)) continue;
            // Canon-side albums don't carry the iTunes AlbumArtist — best we
            // can do is the title alone as the artist segment. Same shape
            // the importer uses for its existing-album dedup key.
            var key = BuildAlbumKey(a.Title, albumArtist: null);
            set.Add(key);
            // Also register the matching artist-bearing variants we'd see
            // from iTunes: if the canon has any album_performer ensemble or
            // conductor we could mirror, but that's a slice-4 concern. For
            // now, accept that a tightly-keyed iTunes import (artist set)
            // won't always hit a canon match — over-proposing is safer than
            // under-proposing.
        }
        return set;
    }

    // ── Artist target collection ─────────────────────────────────────────────

    /// <summary>
    /// Walk every iTunes Composer field, parse to canonical name (via
    /// <see cref="ItunesImportInference.ParseComposer"/>), dedup
    /// case-insensitively, then drop names that already have a fully
    /// populated canon row (approved AND with BirthYear set).
    /// </summary>
    internal static List<string> CollectArtistTargets(
        IReadOnlyList<ItunesTrack> tracks,
        IReadOnlyList<CanonComposer> existingComposers)
    {
        var byName = new Dictionary<string, CanonComposer>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in existingComposers) byName[c.Name] = c;

        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks)
        {
            var parsed = ItunesImportInference.ParseComposer(t.Composer);
            if (parsed is null) continue;
            if (!seen.Add(parsed.Name)) continue;

            if (byName.TryGetValue(parsed.Name, out var canon))
            {
                // Skip only if the canon row is approved AND already carries
                // a BirthYear — that's our "fully populated" signal. A
                // provisional row or a missing-date approved row is still a
                // proposal candidate.
                if (!canon.IsProvisional && !string.IsNullOrEmpty(canon.BirthYear))
                    continue;
            }
            targets.Add(parsed.Name);
        }
        return targets;
    }

    /// <summary>
    /// MB's artist search prefers a single full-name search when possible.
    /// Canon names use "Surname, Givenname" — flip back to "Givenname Surname"
    /// for the MB query and fall back to surname-only when no comma is present.
    /// </summary>
    internal static (string LastName, string? FirstName) SplitComposerNameForQuery(string canonName)
    {
        var comma = canonName.IndexOf(',');
        if (comma < 0)
            return (canonName.Trim(), null);
        var last  = canonName[..comma].Trim();
        var first = canonName[(comma + 1)..].Trim();
        return (last, first.Length == 0 ? null : first);
    }

    // ── Work target collection ──────────────────────────────────────────────

    internal sealed record WorkTarget(string ComposerName, string ParsedPieceTitle, int ExpectedMovementCount);

    /// <summary>
    /// For each unique <c>(composer, parsed-piece-title)</c> across the input
    /// tracks: skip if the canon already covers it (either dot-separator
    /// interpretation resolves cleanly), else build a target with the
    /// expected-movement-count (the number of iTunes tracks that would
    /// land under this piece, in the dominant SubpieceHierarchy
    /// interpretation).
    /// </summary>
    internal static List<KeyValuePair<string, WorkTarget>> CollectWorkTargets(
        IReadOnlyList<ItunesTrack> tracks,
        IReadOnlyList<CanonPiece>  existingPieces,
        PieceReferenceIndex preResolver,
        IReadOnlyDictionary<int, ItunesImportInference.DotSeparatorInterpretation>? dotInterpretations)
    {
        // Group iTunes tracks by their parsed (composer, top-piece-title).
        // ExpectedMovementCount = count of tracks in that bucket. SubpieceHierarchy
        // is the dominant historical interpretation; using it for the
        // movement-count comparison matches MB's "parts" relations cleanly.
        var buckets = new Dictionary<string, (string Composer, string Title, int Count)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var t in tracks)
        {
            var composer = ItunesImportInference.ParseComposer(t.Composer)?.Name;
            if (composer is null) continue;

            var interpretation = dotInterpretations is not null
                                 && dotInterpretations.TryGetValue(t.TrackId, out var chosen)
                ? chosen
                : ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy;

            var parsed = ItunesImportInference.ParseTrackName(t.Name, interpretation);
            if (string.IsNullOrWhiteSpace(parsed.PieceTitle)) continue;

            var key = $"{composer}|{parsed.PieceTitle}".ToLowerInvariant();
            if (buckets.TryGetValue(key, out var existing))
                buckets[key] = (composer, parsed.PieceTitle, existing.Count + 1);
            else
                buckets[key] = (composer, parsed.PieceTitle, 1);
        }

        // Now drop buckets where ResolvesWithoutCreating succeeds under either
        // interpretation. The pre-resolve pass in ItunesImportViewModel runs
        // PER-TRACK; we need a piece-level check. We synthesise a probe
        // ItunesTrack carrying only PieceTitle (no subpath) — if the top
        // piece exists, the work is covered.
        var targets = new List<KeyValuePair<string, WorkTarget>>();
        foreach (var (key, (composer, title, count)) in buckets)
        {
            if (PieceAlreadyResolves(preResolver, composer, title))
                continue;
            targets.Add(new KeyValuePair<string, WorkTarget>(
                key, new WorkTarget(composer, title, count)));
        }
        return targets;
    }

    private static bool PieceAlreadyResolves(
        PieceReferenceIndex preResolver, string composer, string title)
    {
        var probe = new TrackPieceRef { Composer = composer, PieceTitle = title };
        return preResolver.TryResolve(probe) is not null;
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    private static void ReportProgress(
        IProgress<EnrichmentProgress>? progress,
        int albumsResolved, int albumsTotal,
        int artistsResolved, int artistsTotal,
        int worksResolved, int worksTotal)
    {
        progress?.Report(new EnrichmentProgress(
            albumsResolved, albumsTotal,
            artistsResolved, artistsTotal,
            worksResolved, worksTotal));
    }
}
