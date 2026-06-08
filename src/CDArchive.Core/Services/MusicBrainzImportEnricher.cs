using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CDArchive.Core.Services;

/// <summary>
/// Default implementation of <see cref="IMusicBrainzImportEnricher"/>. Composes
/// <see cref="MusicBrainzReference"/>'s rate-limited GET — so the cataloguing
/// flow and the import flow share one process-wide rate-limit gate — and
/// adds the import-shaped JSON mapping layer plus a per-session URL cache.
///
/// <para>Caching strategy: <see cref="ConcurrentDictionary{TKey, TValue}"/>
/// keyed on the full URL, value is the in-flight task. Two callers that hit
/// the same URL concurrently share the request rather than each paying the
/// rate-limit gap. The cache persists for the lifetime of the singleton —
/// re-imports of the same iTunes data in one session don't re-query. To
/// force a refresh, the consumer can drop the singleton (e.g. on app
/// restart) or expose a future Invalidate() method.</para>
///
/// <para>The JSON shape records below are kept <c>internal</c> so test fixtures
/// can drive the mappers directly. The MB API JSON shape is stable enough to
/// rely on at this granularity, but any field we read defensively
/// (null-tolerant + null-coalescing on missing arrays) so a minor MB response
/// change doesn't crash the importer.</para>
/// </summary>
public sealed class MusicBrainzImportEnricher : IMusicBrainzImportEnricher
{
    /// <summary>Includes for the full-detail release fetch — covers every
    /// projection the candidate record carries (label / catno / barcode are
    /// in the base release; recordings provide track lengths; artist-credits
    /// + work-rels provide performer + work linkage; release-rels provide
    /// recording-session events via place + artist relations).</summary>
    internal const string ReleaseDetailIncludes =
        "media+labels+recordings+artist-credits+work-rels+release-rels+place-rels";

    private readonly MusicBrainzReference _mb;
    private readonly ILogger<MusicBrainzImportEnricher> _logger;

    // Per-URL cache. Value-type is Task<object?> so concurrent callers share
    // the in-flight request — first caller starts it, second caller awaits
    // the same task. ConcurrentDictionary's GetOrAdd is the natural fit.
    private readonly ConcurrentDictionary<string, Task<object?>> _cache = new();

    public MusicBrainzImportEnricher(
        MusicBrainzReference mb,
        ILogger<MusicBrainzImportEnricher>? logger = null)
    {
        _mb     = mb;
        _logger = logger ?? NullLogger<MusicBrainzImportEnricher>.Instance;
    }

    // ── Releases ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MbReleaseCandidate>> SearchReleasesAsync(
        string albumTitle,
        string albumArtist,
        IReadOnlyList<TimeSpan> trackLengths,
        int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(albumTitle))
            return Array.Empty<MbReleaseCandidate>();

        // Build a token-based release query rather than a quoted-phrase one.
        //
        // The pre-fix design ANDed release:"<title>" with artist:"<artist>".
        // That broke classical-music matching in two ways:
        //
        //   1. Quoted phrases require an exact substring match — MB's
        //      "Beethoven: Choral Fantasy" never matched iTunes'
        //      "Beethoven Choral Fantasy / Grimaud".
        //   2. The hard `AND artist:` clause filtered out releases whose MB
        //      artist-credit is the composer ("Ludwig van Beethoven") or
        //      "Various Artists" — i.e. nearly every classical release.
        //
        // The token-based form (release:Beethoven release:Choral
        // release:Fantasy …) lets Lucene score each candidate by how many
        // terms it matched, surfacing the best title fit even when MB's
        // title isn't a strict superset of iTunes'. We pass the album
        // artist as additional context (artist:Grimaud) but as soft
        // ranking signal — Lucene's default operator is OR, not AND, so
        // partial matches still surface.
        //
        // Tokens are sanitised through StripLuceneSpecials so a title with
        // ":", "/", "(", etc. doesn't escape the field selector.
        var queryTerms = new List<string>();
        foreach (var t in TokenizeForQuery(albumTitle))
            queryTerms.Add($"release:{t}");
        if (!string.IsNullOrWhiteSpace(albumArtist))
            foreach (var t in TokenizeForQuery(albumArtist))
                queryTerms.Add($"artist:{t}");

        if (queryTerms.Count == 0)
            return Array.Empty<MbReleaseCandidate>();

        // Restrict to releases that include a CD medium. The user's archive
        // is CD rips, so vinyl reissues, cassette, hi-res-digital-only, and
        // SACD-only releases are noise. `format:CD` requires at least one CD
        // medium on the release; multi-format releases (CD+DVD, CD+digital
        // download) still match because they include a CD. The term is ANDed
        // implicitly with the OR-scored title/artist tokens because MB treats
        // a field-qualified term with no preceding operator as a filter
        // alongside the default-OR text terms.
        var query = string.Join(' ', queryTerms) + " AND format:CD";

        // Request 2× the candidates we'll return after ranking so the
        // post-boost ordering has room to surface a strong contender that
        // MB ranked just below a near-miss. Inner cap is 50 to preserve
        // headroom even when the caller asks for the settings max (25).
        // MB's own per-request limit is 100; this stays well within it.
        var requestLimit = Math.Clamp(limit * 2, limit, 50);
        var url = $"https://musicbrainz.org/ws/2/release" +
                  $"?query={HttpUtility.UrlEncode(query)}" +
                  $"&fmt=json&limit={requestLimit}";

        _logger.LogInformation(
            "MB release search: title=\"{Title}\" artist=\"{Artist}\" → query=\"{Query}\"",
            albumTitle, albumArtist, query);

        var response = await FetchAsync<MbReleaseSearchResponse>(url, ct).ConfigureAwait(false);
        if (response?.Releases is null or { Count: 0 })
        {
            _logger.LogInformation(
                "MB release search returned 0 hits for \"{Title}\" / \"{Artist}\"",
                albumTitle, albumArtist);
            return Array.Empty<MbReleaseCandidate>();
        }

        _logger.LogInformation(
            "MB release search returned {Count} hit(s) for \"{Title}\"",
            response.Releases.Count, albumTitle);

        // Map each hit to a candidate. Search hits expose enough scalars
        // (label / catno / barcode / date / country / media[].track-count
        // + artist-credit) to drive ranking + UI display without the
        // per-release detail fetch.
        var candidates = response.Releases
            .Where(r => !string.IsNullOrEmpty(r.Id))
            .Select(MapReleaseSearchHit)
            .OrderByDescending(c => c.Confidence)
            .ToList();

        // Track-count match boost: if the iTunes group's track count matches
        // a candidate exactly, nudge its confidence up; if it's far off,
        // nudge down. Length-sum ranking lives in the planner where the
        // per-detail fetch is paid for.
        if (trackLengths.Count > 0)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var diff = Math.Abs(candidates[i].TrackCount - trackLengths.Count);
                if (diff == 0)
                    candidates[i] = candidates[i] with { Confidence = Math.Min(1.0, candidates[i].Confidence + 0.05) };
                else if (diff > 4)
                    candidates[i] = candidates[i] with { Confidence = Math.Max(0.0, candidates[i].Confidence - 0.10) };
            }
        }

        // Artist-credit boost: when MB's release artist-credit substring-
        // matches the iTunes AlbumArtist, nudge the candidate up. Helps
        // surface conductor/soloist-specific releases that share a title
        // (Karajan vs Bernstein Beethoven 9).
        if (!string.IsNullOrWhiteSpace(albumArtist))
        {
            var needle = albumArtist.Trim().ToLowerInvariant();
            for (int i = 0; i < candidates.Count; i++)
            {
                var haystack = candidates[i].ArtistCredit?.ToLowerInvariant() ?? "";
                if (haystack.Length > 0 && ContainsAnyToken(haystack, needle))
                    candidates[i] = candidates[i] with { Confidence = Math.Min(1.0, candidates[i].Confidence + 0.08) };
            }
        }

        // Re-sort after the boosts and trim to the caller's limit.
        candidates = candidates
            .OrderByDescending(c => c.Confidence)
            .Take(limit)
            .ToList();

        return candidates;
    }

    /// <summary>
    /// Token-split a string for Lucene field-prefixed queries. Strips
    /// Lucene specials, splits on whitespace, drops empties, returns
    /// each remaining token. So "Beethoven: Choral Fantasy / Grimaud"
    /// → ["Beethoven", "Choral", "Fantasy", "Grimaud"]. Tokens are not
    /// quoted — Lucene scores releases by the count of matched
    /// field-prefixed terms.
    /// </summary>
    internal static IEnumerable<string> TokenizeForQuery(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        var cleaned = StripLuceneSpecials(s);
        foreach (var raw in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            // Drop bare connective words that bloat the query without
            // adding signal. Conservative — we only filter universally-
            // weak ones to keep things like "no." or "op." that do help
            // matching catalogues.
            if (string.Equals(t, "the", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(t, "and", StringComparison.OrdinalIgnoreCase)) continue;
            yield return t;
        }
    }

    /// <summary>
    /// Replace Lucene-reserved characters with spaces. Used before
    /// tokenizing a free-text input for a structured field query.
    /// Reserved set per MB / Lucene docs:
    /// <c>+ - &amp;&amp; || ! ( ) { } [ ] ^ " ~ * ? : \ /</c>
    /// </summary>
    internal static string StripLuceneSpecials(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                // Lucene reserved: + - && || ! ( ) { } [ ] ^ " ~ * ? : \ /
                '+' or '-' or '!' or '(' or ')' or '{' or '}' or '['
                or ']' or '^' or '"' or '~' or '*' or '?' or ':'
                or '\\' or '/' or '|' or '&' or ','
                // Period and apostrophe: not reserved by Lucene, but in MB's
                // text analyzer they tend to anchor partial-word tokens that
                // hurt matching ("Vol." vs "Vol"; "'Choral'" vs "Choral").
                // Strip to whitespace so tokenization produces clean
                // alphanumeric terms.
                or '.' or '\'' or '‘' or '’' or '“' or '”'
                  => ' ',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    private static bool ContainsAnyToken(string haystack, string needle)
    {
        foreach (var t in StripLuceneSpecials(needle)
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (t.Length < 3) continue;            // skip noise words
            if (haystack.Contains(t.ToLowerInvariant(), StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public async Task<MbReleaseCandidate?> GetReleaseByMbidAsync(
        string mbReleaseId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(mbReleaseId))
            return null;

        var url = $"https://musicbrainz.org/ws/2/release/{mbReleaseId}" +
                  $"?inc={ReleaseDetailIncludes}&fmt=json";
        var detail = await FetchAsync<MbReleaseDetail>(url, ct).ConfigureAwait(false);
        return detail is null ? null : MapReleaseDetail(detail);
    }

    // ── Artists ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MbArtistSuggestion>> ResolveArtistAsync(
        string lastName,
        string? firstName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(lastName))
            return Array.Empty<MbArtistSuggestion>();

        // Build the query: if firstName is supplied, search the full name;
        // otherwise fall back to surname-only.
        var query = string.IsNullOrWhiteSpace(firstName)
            ? $"artist:\"{EscapeQueryTerm(lastName)}\""
            : $"artist:\"{EscapeQueryTerm(firstName)} {EscapeQueryTerm(lastName)}\"";

        var url = $"https://musicbrainz.org/ws/2/artist" +
                  $"?query={HttpUtility.UrlEncode(query)}" +
                  $"&fmt=json&limit=5";

        var response = await FetchAsync<MbArtistSearchResponse>(url, ct).ConfigureAwait(false);
        if (response?.Artists is null or { Count: 0 })
            return Array.Empty<MbArtistSuggestion>();

        return response.Artists
            .Where(a => !string.IsNullOrEmpty(a.Id))
            // Only Person-type artists — classical composers are persons.
            // Type may be null on minor MB entries; accept those defensively
            // (the user can reject in the review pane).
            .Where(a => a.Type is null or "Person")
            .Select(MapArtistHit)
            .OrderByDescending(a => a.Confidence)
            .ToList();
    }

    // ── Works ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MbWorkSuggestion>> ResolveWorkAsync(
        string composerName,
        string parsedPieceTitle,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(parsedPieceTitle))
            return Array.Empty<MbWorkSuggestion>();

        var queryParts = new List<string>
        {
            $"work:\"{EscapeQueryTerm(parsedPieceTitle)}\"",
        };
        if (!string.IsNullOrWhiteSpace(composerName))
            queryParts.Add($"artist:\"{EscapeQueryTerm(composerName)}\"");
        var query = string.Join(" AND ", queryParts);

        var url = $"https://musicbrainz.org/ws/2/work" +
                  $"?query={HttpUtility.UrlEncode(query)}" +
                  $"&fmt=json&limit=5";

        var response = await FetchAsync<MbWorkSearchResponse>(url, ct).ConfigureAwait(false);
        if (response?.Works is null or { Count: 0 })
            return Array.Empty<MbWorkSuggestion>();

        // For each candidate, fetch its work-rels for the movement list.
        // This pays one rate-limit-slot per candidate; the planner caller
        // typically asks for limit=5, so ~5 extra slots. Acceptable.
        // Tests substitute the per-id fetch deterministically.
        var enriched = new List<MbWorkSuggestion>();
        foreach (var hit in response.Works.Where(w => !string.IsNullOrEmpty(w.Id)))
        {
            var movements = await FetchWorkMovementsAsync(hit.Id!, ct).ConfigureAwait(false);
            enriched.Add(MapWorkHit(hit, movements));
        }
        return enriched.OrderByDescending(w => w.Confidence).ToList();
    }

    private async Task<IReadOnlyList<MbWorkMovement>> FetchWorkMovementsAsync(
        string workId, CancellationToken ct)
    {
        var url = $"https://musicbrainz.org/ws/2/work/{workId}?inc=work-rels&fmt=json";
        var detail = await FetchAsync<MbWorkDetail>(url, ct).ConfigureAwait(false);
        if (detail?.Relations is null) return Array.Empty<MbWorkMovement>();

        int num = 1;
        return detail.Relations
            .Where(r => string.Equals(r.Type, "parts", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(r.Direction, "backward", StringComparison.OrdinalIgnoreCase)
                     && r.Work is not null)
            .OrderBy(r => r.OrderingKey ?? int.MaxValue)
            .Select(r => new MbWorkMovement(
                Number: num++,
                Title:  r.Work!.Title ?? "",
                Tempo:  null))
            .ToList();
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    internal static MbReleaseCandidate MapReleaseSearchHit(MbReleaseSearchHit r)
    {
        var label    = r.LabelInfo?.FirstOrDefault()?.Label?.Name;
        var catno    = r.LabelInfo?.FirstOrDefault()?.CatalogNumber;
        var trackCnt = r.Media?.Sum(m => m.TrackCount ?? 0) ?? 0;
        var discCnt  = r.Media?.Count ?? 0;
        // MB's score is 0..100; normalise to 0..1 for our Confidence.
        var conf     = (r.Score ?? 0) / 100.0;
        var creditStr = JoinArtistCredit(r.ArtistCredit);

        return new MbReleaseCandidate(
            MbReleaseId:     r.Id!,
            Title:           r.Title,
            Label:           label,
            CatalogueNumber: catno,
            Barcode:         r.Barcode,
            Date:            r.Date,
            Country:         r.Country,
            DiscCount:       discCnt,
            TrackCount:      trackCnt,
            Tracks:          Array.Empty<MbReleaseTrack>(),
            RecordingEvents: Array.Empty<MbReleaseEvent>(),
            Credits:         Array.Empty<MbReleaseCredit>(),
            Confidence:      conf,
            ArtistCredit:    creditStr);
    }

    /// <summary>
    /// Joins MB's artist-credit array into the canonical credit string,
    /// e.g. "Beethoven; Grimaud". Uses each entry's join-phrase when
    /// present so the result matches MB's own display form. Returns
    /// null when the array is empty.
    /// </summary>
    internal static string? JoinArtistCredit(IReadOnlyList<MbArtistCreditEntry>? credits)
    {
        if (credits is null or { Count: 0 }) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var c in credits)
        {
            var name = c.Artist?.Name ?? c.Name;
            if (string.IsNullOrEmpty(name)) continue;
            sb.Append(name);
            if (!string.IsNullOrEmpty(c.JoinPhrase))
                sb.Append(c.JoinPhrase);
        }
        var s = sb.ToString().TrimEnd(',', ';', ' ');
        return s.Length == 0 ? null : s;
    }

    internal static MbReleaseCandidate MapReleaseDetail(MbReleaseDetail r)
    {
        var label = r.LabelInfo?.FirstOrDefault()?.Label?.Name;
        var catno = r.LabelInfo?.FirstOrDefault()?.CatalogNumber;

        var tracks = new List<MbReleaseTrack>();
        if (r.Media is not null)
        {
            int discNum = 1;
            foreach (var m in r.Media)
            {
                var mediumDisc = m.Position ?? discNum++;
                if (m.Tracks is null) continue;
                foreach (var t in m.Tracks)
                {
                    tracks.Add(new MbReleaseTrack(
                        DiscNumber:    mediumDisc,
                        TrackNumber:   t.Position ?? 0,
                        Title:         t.Title ?? "",
                        Length:        t.Length is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                        WorkMbid:      null,            // populated via per-track work-rels in a later slice
                        RecordingMbid: t.Recording?.Id));
                }
            }
        }

        var credits = new List<MbReleaseCredit>();
        if (r.ArtistCredit is not null)
        {
            foreach (var ac in r.ArtistCredit)
            {
                var name = ac.Artist?.Name ?? ac.Name;
                if (string.IsNullOrWhiteSpace(name)) continue;
                credits.Add(new MbReleaseCredit(
                    Name:       name,
                    Role:       "Artist",
                    Instrument: null,
                    Mbid:       ac.Artist?.Id));
            }
        }
        if (r.Relations is not null)
        {
            foreach (var rel in r.Relations.Where(rel => rel.Artist is not null))
            {
                credits.Add(new MbReleaseCredit(
                    Name:       rel.Artist!.Name ?? "",
                    Role:       rel.Type ?? "contributor",
                    Instrument: rel.Attributes?.FirstOrDefault(),
                    Mbid:       rel.Artist.Id));
            }
        }

        var events = new List<MbReleaseEvent>();
        if (r.Relations is not null)
        {
            // Recording-event reconstruction. MB exposes recording sessions
            // via /release relations (recorded-at: Place, recorded-by: Artist).
            // For a release that aggregates multiple recording sessions the
            // event list will have multiple entries; the planner picks the
            // first or merges them per the user's review choice.
            foreach (var rel in r.Relations.Where(rel => rel.Place is not null))
            {
                events.Add(new MbReleaseEvent(
                    Date:      rel.Begin ?? rel.End,
                    Venue:     rel.Place!.Name,
                    City:      rel.Place.Area?.Name,
                    Country:   null,
                    Engineers: Array.Empty<string>(),
                    Producers: Array.Empty<string>()));
            }
        }

        // Confidence for a detail fetch is 1.0 — the user has selected this
        // specific release. The search-hit Confidence is what drove ranking;
        // for the detail path we trust the MBID match.
        var trackCnt = tracks.Count;
        var discCnt  = r.Media?.Count ?? 0;
        return new MbReleaseCandidate(
            MbReleaseId:     r.Id!,
            Title:           r.Title,
            Label:           label,
            CatalogueNumber: catno,
            Barcode:         r.Barcode,
            Date:            r.Date,
            Country:         r.Country,
            DiscCount:       discCnt,
            TrackCount:      trackCnt,
            Tracks:          tracks,
            RecordingEvents: events,
            Credits:         credits,
            Confidence:      1.0,
            ArtistCredit:    JoinArtistCredit(r.ArtistCredit));
    }

    internal static MbArtistSuggestion MapArtistHit(MbArtistHit a)
    {
        int? birth = ParseYear(a.LifeSpan?.Begin);
        int? death = ParseYear(a.LifeSpan?.End);
        var conf = (a.Score ?? 0) / 100.0;
        var sortName = a.SortName ?? a.Name ?? "";

        return new MbArtistSuggestion(
            MbArtistId: a.Id!,
            Name:       a.Name ?? "",
            SortName:   sortName,
            BirthYear:  birth,
            DeathYear:  death,
            BirthPlace: a.BeginArea?.Name,
            DeathPlace: a.EndArea?.Name,
            Confidence: conf);
    }

    internal static MbWorkSuggestion MapWorkHit(MbWorkHit hit, IReadOnlyList<MbWorkMovement> movements)
    {
        var conf = (hit.Score ?? 0) / 100.0;
        return new MbWorkSuggestion(
            MbWorkId:    hit.Id!,
            Title:       hit.Title,
            Catalogue:   null,  // MB stores catalogues in work-attributes; mapped in a later slice
            KeyTonality: null,  // ditto
            KeyMode:     null,
            Movements:   movements,
            Confidence:  conf);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch the URL via the shared <see cref="MusicBrainzReference"/>
    /// rate-limited GET, caching the result task per-URL so concurrent
    /// callers share one in-flight request. The cache survives the
    /// lifetime of the singleton; re-imports of the same iTunes data in
    /// one session don't re-query.
    /// </summary>
    private Task<T?> FetchAsync<T>(string url, CancellationToken ct) where T : class
    {
        // GetOrAdd is racy in the worst case (two callers may both build
        // the value factory); we accept the redundancy because the second
        // would still go through the rate-limit gate. The common case is
        // the single caller path.
        var task = _cache.GetOrAdd(url, _ =>
            _mb.RateLimitedGetAsync<T>(url, ct).ContinueWith<object?>(
                t => t.Result,
                TaskContinuationOptions.ExecuteSynchronously));
        return Unwrap<T>(task);
    }

    private static async Task<T?> Unwrap<T>(Task<object?> boxed) where T : class
    {
        var value = await boxed.ConfigureAwait(false);
        return value as T;
    }

    /// <summary>
    /// MB's Lucene-backed query syntax treats quote / backslash as
    /// special. Escape them so a title like Stravinsky's "L'Histoire du
    /// soldat" doesn't terminate the quoted phrase prematurely.
    /// </summary>
    internal static string EscapeQueryTerm(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int? ParseYear(string? dateStr)
    {
        if (string.IsNullOrEmpty(dateStr)) return null;
        var yearPart = dateStr.Split('-')[0];
        return int.TryParse(yearPart, out var y) ? y : null;
    }

    // ── MB JSON shapes ───────────────────────────────────────────────────────
    //
    // Internal so test fixtures can drive the mapping helpers directly.
    // Field naming follows MB's API shape (kebab-case via JsonPropertyName).
    // Every collection is nullable + null-coalesced on read so a sparse MB
    // response doesn't crash the mapper.

    internal sealed class MbReleaseSearchResponse
    {
        public List<MbReleaseSearchHit>? Releases { get; set; }
    }

    internal sealed class MbReleaseSearchHit
    {
        public string? Id { get; set; }
        public int?    Score { get; set; }
        public string? Title { get; set; }
        public string? Date { get; set; }
        public string? Country { get; set; }
        public string? Barcode { get; set; }
        [JsonPropertyName("label-info")]
        public List<MbLabelInfo>? LabelInfo { get; set; }
        public List<MbMedium>? Media { get; set; }
        [JsonPropertyName("artist-credit")]
        public List<MbArtistCreditEntry>? ArtistCredit { get; set; }
    }

    internal sealed class MbReleaseDetail
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Date { get; set; }
        public string? Country { get; set; }
        public string? Barcode { get; set; }
        [JsonPropertyName("label-info")]
        public List<MbLabelInfo>? LabelInfo { get; set; }
        public List<MbMedium>? Media { get; set; }
        [JsonPropertyName("artist-credit")]
        public List<MbArtistCreditEntry>? ArtistCredit { get; set; }
        public List<MbRelation>? Relations { get; set; }
    }

    internal sealed class MbLabelInfo
    {
        [JsonPropertyName("catalog-number")]
        public string? CatalogNumber { get; set; }
        public MbLabel? Label { get; set; }
    }

    internal sealed class MbLabel
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    internal sealed class MbMedium
    {
        public int? Position { get; set; }
        [JsonPropertyName("track-count")]
        public int? TrackCount { get; set; }
        public List<MbTrack>? Tracks { get; set; }
    }

    internal sealed class MbTrack
    {
        public int?    Position { get; set; }
        public string? Title { get; set; }
        public long?   Length { get; set; }   // milliseconds
        public MbRecording? Recording { get; set; }
    }

    internal sealed class MbRecording
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
    }

    internal sealed class MbArtistCreditEntry
    {
        public string? Name { get; set; }
        public MbArtistRef? Artist { get; set; }
        [JsonPropertyName("joinphrase")]
        public string? JoinPhrase { get; set; }
    }

    internal sealed class MbArtistRef
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    internal sealed class MbRelation
    {
        public string? Type { get; set; }
        public string? Direction { get; set; }
        public string? Begin { get; set; }
        public string? End { get; set; }
        [JsonPropertyName("ordering-key")]
        public int? OrderingKey { get; set; }
        public MbArtistRef? Artist { get; set; }
        public MbPlace? Place { get; set; }
        public MbWorkHit? Work { get; set; }
        public List<string>? Attributes { get; set; }
    }

    internal sealed class MbPlace
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public MbArea? Area { get; set; }
    }

    internal sealed class MbArea
    {
        public string? Name { get; set; }
    }

    internal sealed class MbArtistSearchResponse
    {
        public List<MbArtistHit>? Artists { get; set; }
    }

    internal sealed class MbArtistHit
    {
        public string? Id { get; set; }
        public int?    Score { get; set; }
        public string? Name { get; set; }
        [JsonPropertyName("sort-name")]
        public string? SortName { get; set; }
        public string? Type { get; set; }
        [JsonPropertyName("life-span")]
        public MbLifeSpan? LifeSpan { get; set; }
        [JsonPropertyName("begin-area")]
        public MbArea? BeginArea { get; set; }
        [JsonPropertyName("end-area")]
        public MbArea? EndArea { get; set; }
    }

    internal sealed class MbLifeSpan
    {
        public string? Begin { get; set; }
        public string? End { get; set; }
    }

    internal sealed class MbWorkSearchResponse
    {
        public List<MbWorkHit>? Works { get; set; }
    }

    internal sealed class MbWorkHit
    {
        public string? Id { get; set; }
        public int?    Score { get; set; }
        public string? Title { get; set; }
    }

    internal sealed class MbWorkDetail
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public List<MbRelation>? Relations { get; set; }
    }
}
