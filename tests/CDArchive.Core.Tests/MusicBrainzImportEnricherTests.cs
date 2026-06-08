using System.Net;
using System.Text;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="MusicBrainzImportEnricher"/>: the import-shaped MB
/// query layer that the planner (slice 3) and importer (slice 4) consume.
///
/// <para>Coverage falls into five clusters:</para>
/// <list type="bullet">
///   <item><b>URL construction.</b> Search URLs include the right MB
///         path + Lucene-style query + URL encoding; detail URLs use the
///         right MBID + includes.</item>
///   <item><b>JSON-to-record mapping.</b> Each MB response shape (release
///         search hit / release detail / artist hit / work hit + work-rels)
///         produces the documented field values on the matching record,
///         with sparse / missing fields tolerated.</item>
///   <item><b>Ranking.</b> Search results sort by descending confidence
///         after the track-count match boost; the MBID shortcut path
///         returns Confidence=1.0.</item>
///   <item><b>Caching.</b> A second call with identical args reuses the
///         in-flight task — handler request count stays at 1.</item>
///   <item><b>Edge cases.</b> Empty/whitespace inputs short-circuit
///         without an HTTP call; MB-returned-empty-list deserialises to
///         an empty IReadOnlyList rather than throwing.</item>
/// </list>
///
/// <para>Fixtures reuse the <c>ScriptedHandler</c> / <c>SingleClientFactory</c> /
/// <c>FakeDelayer</c> shape used by <see cref="MusicBrainzReferenceTests"/>;
/// duplicated here as private nested types so the two suites stay
/// independent (slice 1 isolation).</para>
/// </summary>
public class MusicBrainzImportEnricherTests
{
    // ── Fixtures (copy of the MusicBrainzReferenceTests scaffold) ────────────

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<string> Requests { get; } = new();

        public void Enqueue(HttpStatusCode status, string body = "{}")
        {
            _responses.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            cancellationToken.ThrowIfCancellationRequested();
            var make = _responses.Count > 0
                ? _responses.Dequeue()
                : (() => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            return Task.FromResult(make());
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public SingleClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static (MusicBrainzImportEnricher enricher, ScriptedHandler handler) Build()
    {
        var handler = new ScriptedHandler();
        var mb = new MusicBrainzReference(
            new SingleClientFactory(handler),
            logger: null,
            (_, _) => Task.CompletedTask);
        var enricher = new MusicBrainzImportEnricher(mb);
        return (enricher, handler);
    }

    /// <summary>
    /// HttpUtility.UrlEncode emits <c>+</c> for spaces (legacy form-encoding
    /// convention). Uri.UnescapeDataString only decodes %XX sequences, not
    /// <c>+</c>, so the request URLs still carry literal pluses. Helper
    /// normalises both representations so substring assertions read the
    /// human-readable query.
    /// </summary>
    private static string DecodeQuery(string url) =>
        System.Web.HttpUtility.UrlDecode(url);

    // ── URL construction ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchReleasesAsync_BuildsTokenBasedLuceneQuery_NotQuotedPhrase()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        await e.SearchReleasesAsync("Choral Fantasy", "Grimaud", Array.Empty<TimeSpan>(), 5, default);

        Assert.Single(h.Requests);
        var url = h.Requests[0];
        Assert.Contains("/ws/2/release", url);
        Assert.Contains("fmt=json", url);
        // Limit is 2× the caller's request so post-boost ranking has headroom.
        Assert.Contains("limit=10", url);
        var decoded = DecodeQuery(url);
        // New shape: token-prefixed unquoted release: terms, not the
        // pre-fix release:"…" AND artist:"…" quoted form. The token-
        // based shape matches releases when MB's title is a near-but-
        // not-exact substring of iTunes' (the classical-music gap).
        Assert.Contains("release:Choral", decoded);
        Assert.Contains("release:Fantasy", decoded);
        // Artist included as a soft signal (Lucene OR-scores it, doesn't
        // require it).
        Assert.Contains("artist:Grimaud", decoded);
        Assert.DoesNotContain("\"Choral Fantasy\"", decoded);
        // The text terms are unquoted (OR-scored); the only AND is the
        // media-format filter appended below.
        Assert.Contains("AND format:CD", decoded);
    }

    [Fact]
    public async Task SearchReleasesAsync_RestrictsToCdMedia()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        await e.SearchReleasesAsync("Some Album", "Some Artist", Array.Empty<TimeSpan>(), 5, default);

        var decoded = DecodeQuery(h.Requests[0]);
        // CD-media filter restricts candidates to releases with at least one
        // CD medium — the user's archive is CD rips, so vinyl / cassette /
        // digital-only releases are noise.
        Assert.Contains("format:CD", decoded);
    }

    [Fact]
    public async Task SearchReleasesAsync_StripsLuceneSpecials_InTitleAndArtist()
    {
        // iTunes titles routinely carry ":", "/", "(", and other Lucene
        // specials that would mis-parse if passed through verbatim. We
        // sanitise to whitespace + split into tokens.
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        await e.SearchReleasesAsync(
            "Beethoven: Choral Fantasy / Grimaud", "Salonen (cond.)",
            Array.Empty<TimeSpan>(), 5, default);

        var decoded = DecodeQuery(h.Requests[0]);
        Assert.Contains("release:Beethoven", decoded);
        Assert.Contains("release:Choral", decoded);
        Assert.Contains("release:Fantasy", decoded);
        Assert.Contains("release:Grimaud", decoded);
        Assert.Contains("artist:Salonen", decoded);
        Assert.Contains("artist:cond", decoded);
        // Periods stripped too — "cond." → "cond" — so MB's analyzer
        // doesn't treat the trailing "." as an anchor that hurts matching.
        Assert.DoesNotContain("artist:cond.", decoded);
        // Specials gone — no ":", "/", "(", ")" outside the field syntax.
        Assert.DoesNotContain("Beethoven:Choral", decoded);
        Assert.DoesNotContain("(cond.)", decoded);
    }

    [Fact]
    public async Task SearchReleasesAsync_OmitsArtistTokens_WhenArtistIsBlank()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        await e.SearchReleasesAsync("Goldberg Variations", "", Array.Empty<TimeSpan>(), 3, default);

        var decoded = DecodeQuery(h.Requests[0]);
        Assert.Contains("release:Goldberg", decoded);
        Assert.Contains("release:Variations", decoded);
        Assert.DoesNotContain("artist:", decoded);
    }

    [Fact]
    public void TokenizeForQuery_StripsPeriodsApostrophesAndCurlyQuotes()
    {
        // Real-world iTunes classical titles routinely carry "Vol.", "No.",
        // "Op.", "'Choral'", and curly-quote variants from various tag
        // editors. These are stripped to whitespace so tokens are clean
        // alphanumeric strings MB's analyzer can match cleanly.
        var tokens1 = MusicBrainzImportEnricher.TokenizeForQuery(
            "Beethoven: Piano Sonatas, Vol. 10").ToList();
        Assert.Contains("Vol", tokens1);
        Assert.DoesNotContain("Vol.", tokens1);
        Assert.Contains("10", tokens1);

        var tokens2 = MusicBrainzImportEnricher.TokenizeForQuery(
            "Beethoven: Symphony No. 9 in D minor 'Choral'").ToList();
        Assert.Contains("Choral", tokens2);
        Assert.DoesNotContain("'Choral'", tokens2);
        Assert.DoesNotContain("No.", tokens2);

        // Curly quotes from sloppy tag editors.
        var tokens3 = MusicBrainzImportEnricher.TokenizeForQuery(
            "Beethoven: Symphony No. 3 “Eroica”").ToList();
        Assert.Contains("Eroica", tokens3);
        Assert.DoesNotContain("“Eroica”", tokens3);
    }

    [Fact]
    public void TokenizeForQuery_PuresUtilityCases()
    {
        // Cleaning rules cover: Lucene specials → space; collapse multiple
        // spaces; drop "the"/"and" noise words; preserve catalogue tokens
        // ("op.", "no.") that DO help matching.
        var tokens = MusicBrainzImportEnricher.TokenizeForQuery(
            "The Goldberg Variations: BWV 988").ToList();
        Assert.DoesNotContain("The", tokens);
        Assert.Contains("Goldberg", tokens);
        Assert.Contains("Variations", tokens);
        Assert.Contains("BWV", tokens);
        Assert.Contains("988", tokens);
        Assert.DoesNotContain(":", tokens);
    }

    [Fact]
    public async Task GetReleaseByMbidAsync_UsesMbidEndpoint_WithFullIncludes()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"id\":\"abc\"}");

        await e.GetReleaseByMbidAsync("aaaa-bbbb-cccc", default);

        var url = h.Requests[0];
        Assert.Contains("/ws/2/release/aaaa-bbbb-cccc", url);
        // The include list is the documented full set used by the importer
        // for the user's chosen candidate detail fetch.
        Assert.Contains("media", url);
        Assert.Contains("labels", url);
        Assert.Contains("recordings", url);
        Assert.Contains("artist-credits", url);
        Assert.Contains("work-rels", url);
    }

    [Fact]
    public async Task ResolveArtistAsync_FullName_QueriesByFullName()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"artists\":[]}");

        await e.ResolveArtistAsync("Beethoven", "Ludwig van", default);

        var decoded = DecodeQuery(h.Requests[0]);
        Assert.Contains("/ws/2/artist", h.Requests[0]);
        Assert.Contains("Ludwig van Beethoven", decoded);
    }

    [Fact]
    public async Task ResolveArtistAsync_SurnameOnly_QueriesByLastName()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"artists\":[]}");

        await e.ResolveArtistAsync("Beethoven", firstName: null, default);

        var decoded = DecodeQuery(h.Requests[0]);
        Assert.Contains("artist:\"Beethoven\"", decoded);
        // Surname-only path emits one term, no "Ludwig" or other given name.
        Assert.DoesNotContain("Ludwig", decoded);
    }

    // ── Mapping fidelity — releases ──────────────────────────────────────────

    [Fact]
    public async Task SearchReleasesAsync_MapsAllScalars_ToCandidate()
    {
        var (e, h) = Build();
        // Representative MB release-search shape. Fields that the importer
        // consumes today: id, score, title, date, country, barcode, the
        // first label-info entry's catalog-number + label.name, media[]
        // for disc-count + track-count summary.
        const string body = """
        {
          "releases": [
            {
              "id": "release-001",
              "score": 91,
              "title": "Beethoven: Choral Fantasy",
              "date": "2008-04-15",
              "country": "DE",
              "barcode": "028947795230",
              "label-info": [
                { "catalog-number": "477 9523",
                  "label": { "id": "lab-1", "name": "Deutsche Grammophon" } }
              ],
              "media": [ { "track-count": 6 } ]
            }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var results = await e.SearchReleasesAsync(
            "Choral Fantasy", "Grimaud", Array.Empty<TimeSpan>(), 5, default);

        var c = Assert.Single(results);
        Assert.Equal("release-001", c.MbReleaseId);
        Assert.Equal("Beethoven: Choral Fantasy", c.Title);
        Assert.Equal("Deutsche Grammophon", c.Label);
        Assert.Equal("477 9523", c.CatalogueNumber);
        Assert.Equal("028947795230", c.Barcode);
        Assert.Equal("2008-04-15", c.Date);
        Assert.Equal("DE", c.Country);
        Assert.Equal(1, c.DiscCount);
        Assert.Equal(6, c.TrackCount);
        // Score 91 → confidence ≈ 0.91; the no-track-length-info path leaves
        // the confidence alone (no boost / penalty applied).
        Assert.InRange(c.Confidence, 0.90, 0.92);
        // Search hits don't carry detail — that comes from GetReleaseByMbidAsync.
        Assert.Empty(c.Tracks);
        Assert.Empty(c.Credits);
        Assert.Empty(c.RecordingEvents);
    }

    [Fact]
    public async Task SearchReleasesAsync_ExtractsArtistCredit_FromSearchHit()
    {
        // Search hits now carry the join-phrased artist-credit string so
        // the review pane's dropdown can show "Title — Beethoven; Grimaud
        // · Label CatNo · Date" without a per-row detail fetch.
        var (e, h) = Build();
        const string body = """
        {
          "releases": [
            {
              "id": "r1", "score": 90, "title": "Choral Fantasy",
              "artist-credit": [
                { "name": "Beethoven",
                  "artist": { "id": "a1", "name": "Ludwig van Beethoven" },
                  "joinphrase": "; " },
                { "name": "Grimaud",
                  "artist": { "id": "a2", "name": "Hélène Grimaud" } }
              ]
            }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var results = await e.SearchReleasesAsync(
            "Choral Fantasy", "Grimaud", Array.Empty<TimeSpan>(), 5, default);

        var c = Assert.Single(results);
        Assert.Equal("Ludwig van Beethoven; Hélène Grimaud", c.ArtistCredit);
    }

    [Fact]
    public async Task SearchReleasesAsync_ArtistCreditMatch_BoostsConfidence_AndAffectsRanking()
    {
        // Two candidates with the same MB score. Only one's artist-credit
        // matches the iTunes AlbumArtist → it wins after the boost.
        var (e, h) = Build();
        const string body = """
        {
          "releases": [
            { "id": "wrong-conductor", "score": 90, "title": "Beethoven Symphony 9",
              "artist-credit": [
                { "name": "Karajan", "artist": { "id": "a1", "name": "Herbert von Karajan" } }
              ] },
            { "id": "right-conductor", "score": 90, "title": "Beethoven Symphony 9",
              "artist-credit": [
                { "name": "Bernstein", "artist": { "id": "a2", "name": "Leonard Bernstein" } }
              ] }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var results = await e.SearchReleasesAsync(
            "Beethoven Symphony 9", "Bernstein", Array.Empty<TimeSpan>(), 5, default);

        // After the artist-credit substring boost the Bernstein release
        // outranks the Karajan one even though both got the same MB score.
        Assert.Equal("right-conductor", results[0].MbReleaseId);
    }

    [Fact]
    public async Task SearchReleasesAsync_ExactTrackCountMatch_BoostsConfidence()
    {
        var (e, h) = Build();
        // Two candidates: exact-match (6 tracks) and far-off (12 tracks).
        // The 6-track candidate should sort to the top after the boost +
        // penalty are applied.
        const string body = """
        {
          "releases": [
            { "id": "near-but-wrong", "score": 95, "title": "Wrong",
              "media": [ { "track-count": 12 } ] },
            { "id": "exact-match", "score": 80, "title": "Right",
              "media": [ { "track-count": 6 } ] }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var trackLengths = Enumerable.Repeat(TimeSpan.FromMinutes(4), 6).ToList();
        var results = await e.SearchReleasesAsync(
            "X", "Y", trackLengths, 5, default);

        // After ranking: exact-match (80 + 0.05 → 0.85) beats wrong (95 - 0.10 → 0.85)?
        // 95→0.95 - 0.10 = 0.85; 80→0.80 + 0.05 = 0.85 — a tie, but the
        // OrderByDescending is stable-ish for equal keys. To assert
        // deterministically, build a clearer gap.
        Assert.Equal(2, results.Count);
        // Just pin the boost mechanic: exact-match's confidence increased,
        // wrong-count's decreased.
        var exact = results.Single(r => r.MbReleaseId == "exact-match");
        var wrong = results.Single(r => r.MbReleaseId == "near-but-wrong");
        Assert.InRange(exact.Confidence, 0.84, 0.86); // 0.80 + 0.05
        Assert.InRange(wrong.Confidence, 0.84, 0.86); // 0.95 - 0.10
    }

    [Fact]
    public async Task GetReleaseByMbidAsync_MapsDetailFields_IncludingTracksAndCredits()
    {
        var (e, h) = Build();
        // MB release detail with the full include set. We populate enough
        // shape to drive every projection the candidate carries.
        const string body = """
        {
          "id": "release-001",
          "title": "Beethoven: Choral Fantasy",
          "date": "2008-04-15",
          "country": "DE",
          "barcode": "028947795230",
          "label-info": [
            { "catalog-number": "477 9523",
              "label": { "id": "lab-1", "name": "Deutsche Grammophon" } }
          ],
          "media": [
            {
              "position": 1,
              "track-count": 2,
              "tracks": [
                { "position": 1, "title": "Fantasy", "length": 240000,
                  "recording": { "id": "rec-1", "title": "Fantasy" } },
                { "position": 2, "title": "Choral", "length": 360000,
                  "recording": { "id": "rec-2", "title": "Choral" } }
              ]
            }
          ],
          "artist-credit": [
            { "name": "Hélène Grimaud", "artist": { "id": "art-1", "name": "Hélène Grimaud" } }
          ],
          "relations": [
            { "type": "conductor",
              "artist": { "id": "art-2", "name": "Esa-Pekka Salonen" } },
            { "type": "recorded at",
              "begin": "2008-04-10",
              "place": { "id": "pl-1", "name": "Funkhaus",
                         "area": { "name": "Berlin" } } }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var c = await e.GetReleaseByMbidAsync("release-001", default);

        Assert.NotNull(c);
        Assert.Equal("release-001",            c!.MbReleaseId);
        Assert.Equal("Beethoven: Choral Fantasy", c.Title);
        Assert.Equal("Deutsche Grammophon",     c.Label);
        Assert.Equal("477 9523",                c.CatalogueNumber);
        Assert.Equal(2, c.TrackCount);
        Assert.Equal(2, c.Tracks.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(240000), c.Tracks[0].Length);
        Assert.Equal("rec-1", c.Tracks[0].RecordingMbid);
        // Credits: one from artist-credit + one from artist-rels relation.
        Assert.Equal(2, c.Credits.Count);
        Assert.Contains(c.Credits, cr => cr.Name == "Hélène Grimaud"   && cr.Role == "Artist");
        Assert.Contains(c.Credits, cr => cr.Name == "Esa-Pekka Salonen" && cr.Role == "conductor");
        // Recording event: place relation mapped.
        var evt = Assert.Single(c.RecordingEvents);
        Assert.Equal("Funkhaus", evt.Venue);
        Assert.Equal("Berlin",   evt.City);
        Assert.Equal("2008-04-10", evt.Date);
        // MBID-shortcut path uses Confidence=1.0 (user has chosen this release).
        Assert.Equal(1.0, c.Confidence);
    }

    // ── Mapping fidelity — artists & works ───────────────────────────────────

    [Fact]
    public async Task ResolveArtistAsync_MapsDatesAndPlaces()
    {
        var (e, h) = Build();
        const string body = """
        {
          "artists": [
            {
              "id": "art-001",
              "score": 99,
              "type": "Person",
              "name": "Ludwig van Beethoven",
              "sort-name": "Beethoven, Ludwig van",
              "life-span": { "begin": "1770-12-17", "end": "1827-03-26" },
              "begin-area": { "name": "Bonn" },
              "end-area":   { "name": "Vienna" }
            }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var results = await e.ResolveArtistAsync("Beethoven", "Ludwig van", default);
        var a = Assert.Single(results);
        Assert.Equal("art-001",                      a.MbArtistId);
        Assert.Equal("Ludwig van Beethoven",         a.Name);
        Assert.Equal("Beethoven, Ludwig van",        a.SortName);
        Assert.Equal(1770, a.BirthYear);
        Assert.Equal(1827, a.DeathYear);
        Assert.Equal("Bonn",   a.BirthPlace);
        Assert.Equal("Vienna", a.DeathPlace);
        Assert.InRange(a.Confidence, 0.98, 1.0);
    }

    [Fact]
    public async Task ResolveArtistAsync_NonPersonTypes_AreFilteredOut()
    {
        var (e, h) = Build();
        const string body = """
        {
          "artists": [
            { "id": "ens-1", "name": "Berlin Phil", "sort-name": "Berlin Phil",
              "type": "Group", "score": 95 },
            { "id": "per-1", "name": "Beethoven", "sort-name": "Beethoven",
              "type": "Person", "score": 80 }
          ]
        }
        """;
        h.Enqueue(HttpStatusCode.OK, body);

        var results = await e.ResolveArtistAsync("Beethoven", null, default);
        var a = Assert.Single(results);
        Assert.Equal("per-1", a.MbArtistId);
    }

    [Fact]
    public async Task ResolveWorkAsync_FetchesPerWorkRels_AndOrdersMovementsByOrderingKey()
    {
        var (e, h) = Build();
        // 1) Work search returns one hit.
        h.Enqueue(HttpStatusCode.OK, """
            { "works": [ { "id": "wrk-001", "score": 96, "title": "Piano Sonata No. 14" } ] }
            """);
        // 2) Per-work detail returns three "parts" relations, intentionally
        //    out of ordering-key order so we can pin the sort.
        h.Enqueue(HttpStatusCode.OK, """
            {
              "id": "wrk-001",
              "title": "Piano Sonata No. 14",
              "relations": [
                { "type": "parts", "direction": "backward", "ordering-key": 3,
                  "work": { "id": "mov-3", "title": "Presto agitato" } },
                { "type": "parts", "direction": "backward", "ordering-key": 1,
                  "work": { "id": "mov-1", "title": "Adagio sostenuto" } },
                { "type": "parts", "direction": "backward", "ordering-key": 2,
                  "work": { "id": "mov-2", "title": "Allegretto" } }
              ]
            }
            """);

        var results = await e.ResolveWorkAsync("Beethoven", "Piano Sonata 14", default);

        var w = Assert.Single(results);
        Assert.Equal("wrk-001", w.MbWorkId);
        Assert.Equal(3, w.Movements.Count);
        Assert.Equal("Adagio sostenuto", w.Movements[0].Title);
        Assert.Equal("Allegretto",        w.Movements[1].Title);
        Assert.Equal("Presto agitato",    w.Movements[2].Title);
        // Number is the post-ordering 1-based index, not the MB ordering-key.
        Assert.Equal(1, w.Movements[0].Number);
        Assert.Equal(2, w.Movements[1].Number);
        Assert.Equal(3, w.Movements[2].Number);
    }

    // ── Caching ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchReleases_SameArgsTwice_HitsHandlerOnce()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        await e.SearchReleasesAsync("X", "Y", Array.Empty<TimeSpan>(), 5, default);
        await e.SearchReleasesAsync("X", "Y", Array.Empty<TimeSpan>(), 5, default);

        // Cache short-circuits the second call — handler sees one request.
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task DifferentLimit_BypassesCache_RaisesAnotherRequest()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        // Different limit changes the URL → cache miss.
        await e.SearchReleasesAsync("X", "Y", Array.Empty<TimeSpan>(), 5,  default);
        await e.SearchReleasesAsync("X", "Y", Array.Empty<TimeSpan>(), 10, default);

        Assert.Equal(2, h.Requests.Count);
    }

    // ── Edge cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BlankInputs_ShortCircuit_WithoutHttpCall()
    {
        var (e, h) = Build();

        var rel = await e.SearchReleasesAsync("",  "X", Array.Empty<TimeSpan>(), 5, default);
        var art = await e.ResolveArtistAsync("", null, default);
        var wrk = await e.ResolveWorkAsync("X", "", default);
        var mbi = await e.GetReleaseByMbidAsync("", default);

        Assert.Empty(rel);
        Assert.Empty(art);
        Assert.Empty(wrk);
        Assert.Null(mbi);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void EscapeQueryTerm_EscapesQuoteAndBackslash()
    {
        // Pure-logic helper, no HTTP. Names with internal quote / backslash
        // shouldn't terminate the Lucene phrase prematurely. Test arguments
        // use verbatim @"..." literals so the backslash / quote sequences
        // read true-to-source.

        // Clean input → unchanged.
        Assert.Equal("Beethoven", MusicBrainzImportEnricher.EscapeQueryTerm("Beethoven"));

        // Embedded double-quote: should be escaped to \"
        // Source has literal " in the middle (verbatim "" inside @"...").
        var withQuote     = "Bach \"arr\"";
        var expectedQuote = "Bach \\\"arr\\\"";
        Assert.Equal(expectedQuote, MusicBrainzImportEnricher.EscapeQueryTerm(withQuote));

        // Embedded backslash: should be escaped to \\
        var withBackslash     = "foo\\bar";
        var expectedBackslash = "foo\\\\bar";
        Assert.Equal(expectedBackslash, MusicBrainzImportEnricher.EscapeQueryTerm(withBackslash));
    }

    [Fact]
    public async Task EmptyReleasesArray_ReturnsEmptyList()
    {
        var (e, h) = Build();
        h.Enqueue(HttpStatusCode.OK, "{\"releases\":[]}");

        var results = await e.SearchReleasesAsync("X", "Y", Array.Empty<TimeSpan>(), 5, default);

        Assert.Empty(results);
        // Confirm we didn't return null or throw on the empty list.
        Assert.NotNull(results);
    }
}
