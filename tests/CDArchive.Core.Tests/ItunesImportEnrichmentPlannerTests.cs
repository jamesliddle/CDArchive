using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="ItunesImportEnrichmentPlanner"/>: the orchestration
/// layer between the iTunes import view-model and the MB enricher (slice 2).
/// The planner doesn't make HTTP calls itself — it drives an
/// <see cref="IMusicBrainzImportEnricher"/>, so tests script a fake enricher
/// that records every call and returns canned responses.
///
/// <para>Coverage clusters:</para>
/// <list type="bullet">
///   <item><b>Album grouping.</b> Tracks group by <c>(Album, AlbumArtist)</c>,
///         loose tracks (no Album) excluded.</item>
///   <item><b>Canon skip.</b> Approved albums skipped from album pass;
///         approved+populated composers skipped from artist pass;
///         already-resolvable pieces skipped from work pass.</item>
///   <item><b>Movement-count mismatch.</b> Top work candidate's movement
///         count compared to iTunes-track count under that piece — flagged
///         on the proposal AND emits a warning.</item>
///   <item><b>Progress + cancel.</b> IProgress fires per resolved item;
///         cancellation propagates to the enricher and throws.</item>
///   <item><b>Preferred index.</b> Highest-confidence candidate becomes
///         <see cref="AlbumEnrichmentProposal.PreferredIndex"/>; -1 when
///         no candidates returned.</item>
/// </list>
/// </summary>
public class ItunesImportEnrichmentPlannerTests
{
    // ── Fake enricher ────────────────────────────────────────────────────────

    private sealed class FakeEnricher : IMusicBrainzImportEnricher
    {
        public List<(string Title, string Artist)> ReleaseSearchCalls { get; } = new();
        public List<string> ReleaseByMbidCalls { get; } = new();
        public List<(string Last, string? First)> ArtistCalls { get; } = new();
        public List<(string Composer, string Title)> WorkCalls { get; } = new();

        public Func<string, string, IReadOnlyList<MbReleaseCandidate>> SearchReleasesReturns { get; set; }
            = (_, _) => Array.Empty<MbReleaseCandidate>();
        public Func<string, MbReleaseCandidate?> GetReleaseByMbidReturns { get; set; }
            = _ => null;
        public Func<string, string?, IReadOnlyList<MbArtistSuggestion>> ResolveArtistReturns { get; set; }
            = (_, _) => Array.Empty<MbArtistSuggestion>();
        public Func<string, string, IReadOnlyList<MbWorkSuggestion>> ResolveWorkReturns { get; set; }
            = (_, _) => Array.Empty<MbWorkSuggestion>();

        public Task<IReadOnlyList<MbReleaseCandidate>> SearchReleasesAsync(
            string albumTitle, string albumArtist,
            IReadOnlyList<TimeSpan> trackLengths, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseSearchCalls.Add((albumTitle, albumArtist));
            return Task.FromResult(SearchReleasesReturns(albumTitle, albumArtist));
        }

        public Task<MbReleaseCandidate?> GetReleaseByMbidAsync(string mbReleaseId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ReleaseByMbidCalls.Add(mbReleaseId);
            return Task.FromResult(GetReleaseByMbidReturns(mbReleaseId));
        }

        public Task<IReadOnlyList<MbArtistSuggestion>> ResolveArtistAsync(
            string lastName, string? firstName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ArtistCalls.Add((lastName, firstName));
            return Task.FromResult(ResolveArtistReturns(lastName, firstName));
        }

        public Task<IReadOnlyList<MbWorkSuggestion>> ResolveWorkAsync(
            string composerName, string parsedPieceTitle, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            WorkCalls.Add((composerName, parsedPieceTitle));
            return Task.FromResult(ResolveWorkReturns(composerName, parsedPieceTitle));
        }
    }

    private static ItunesTrack Track(
        int id, string name, string? composer = null,
        string? album = null, string? albumArtist = null,
        int? durationMs = null, string? mbReleaseId = null,
        string? artist = null) =>
        new(id, PersistentId: null, DiscNumber: 1, TrackNumber: id, Name: name,
            DurationMs: durationMs, Genre: null, Composer: composer,
            Album: album, AlbumArtist: albumArtist, Artist: artist,
            DateAdded: null, Location: null,
            MbReleaseId: mbReleaseId, MbRecordingId: null, MbWorkId: null);

    private static MbReleaseCandidate Release(string id, double conf, int tracks = 0) =>
        new(MbReleaseId: id, Title: id, Label: null, CatalogueNumber: null, Barcode: null,
            Date: null, Country: null, DiscCount: 1, TrackCount: tracks,
            Tracks: Array.Empty<MbReleaseTrack>(),
            RecordingEvents: Array.Empty<MbReleaseEvent>(),
            Credits: Array.Empty<MbReleaseCredit>(),
            Confidence: conf);

    private static MbArtistSuggestion Artist(string id, double conf, int? birthYear = null) =>
        new(MbArtistId: id, Name: id, SortName: id, BirthYear: birthYear, DeathYear: null,
            BirthPlace: null, DeathPlace: null, Confidence: conf);

    private static MbWorkSuggestion Work(string id, double conf, int movementCount = 0)
    {
        var movements = new List<MbWorkMovement>();
        for (int i = 1; i <= movementCount; i++)
            movements.Add(new MbWorkMovement(i, $"Mov {i}", Tempo: null));
        return new MbWorkSuggestion(
            MbWorkId: id, Title: id, Catalogue: null, KeyTonality: null, KeyMode: null,
            Movements: movements, Confidence: conf);
    }

    // ── Album pass ───────────────────────────────────────────────────────────

    [Fact]
    public async Task EmptyInput_ReturnsEmptyPlan_NoEnricherCalls()
    {
        var fake = new FakeEnricher();
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var plan = await planner.PlanAsync(
            Array.Empty<ItunesTrack>(),
            Array.Empty<CanonComposer>(),
            Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(),
            dotInterpretations: null, progress: null);

        Assert.Empty(plan.Albums);
        Assert.Empty(plan.Artists);
        Assert.Empty(plan.Works);
        Assert.Empty(plan.Warnings);
        Assert.Empty(fake.ReleaseSearchCalls);
        Assert.Empty(fake.ArtistCalls);
        Assert.Empty(fake.WorkCalls);
    }

    [Fact]
    public async Task AlbumPass_GroupsByAlbumAndArtist_OneSearchPerGroup()
    {
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9, tracks: 2) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", composer: "X (1900-1970)", album: "Album A", albumArtist: "Karajan"),
            Track(2, "T2", composer: "X (1900-1970)", album: "Album A", albumArtist: "Karajan"),
            Track(3, "T3", composer: "X (1900-1970)", album: "Album B", albumArtist: "Bernstein"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(),
            dotInterpretations: null, progress: null);

        Assert.Equal(2, plan.Albums.Count);
        Assert.Equal(2, fake.ReleaseSearchCalls.Count);
        // Each group queried once with its title. Artist hint now combines
        // composer + AlbumArtist (the planner prepends the composer hint when
        // there's a single shared composer across the album's tracks).
        Assert.Contains(fake.ReleaseSearchCalls,
            c => c.Title == "Album A" && c.Artist.Contains("Karajan"));
        Assert.Contains(fake.ReleaseSearchCalls,
            c => c.Title == "Album B" && c.Artist.Contains("Bernstein"));
        // Track-count attached.
        var groupA = plan.Albums.Single(a => a.ItunesAlbumTitle == "Album A");
        Assert.Equal(2, groupA.TrackCount);
    }

    [Fact]
    public async Task LooseTracks_ExcludedFromAlbumPass()
    {
        // User scoped MB enrichment to album metadata only (no artist /
        // work passes). Loose tracks have no album, so they produce no
        // proposals at all — empty plan.
        var fake = new FakeEnricher
        {
            ResolveArtistReturns = (_, _) => new[] { Artist("a1", 0.95) },
            ResolveWorkReturns   = (_, _) => new[] { Work("w1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            // album=null → loose track.
            Track(1, "Some Sonata", composer: "Beethoven (1770-1827)"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(),
            dotInterpretations: null, progress: null);

        Assert.Empty(plan.Albums);
        Assert.Empty(fake.ReleaseSearchCalls);
        // Artist + work passes skipped → these collections always empty.
        Assert.Empty(plan.Artists);
        Assert.Empty(plan.Works);
        Assert.Empty(fake.ArtistCalls);
        Assert.Empty(fake.WorkCalls);
    }

    [Fact]
    public async Task ApprovedCanonAlbum_SkippedFromAlbumPass_EmitsWarning()
    {
        var fake = new FakeEnricher();
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var existing = new[]
        {
            new CanonAlbum { Title = "Album A", IsProvisional = false },
        };
        var tracks = new[]
        {
            Track(1, "T1", album: "Album A", albumArtist: ""),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            existing, dotInterpretations: null, progress: null);

        Assert.Empty(plan.Albums);
        Assert.Empty(fake.ReleaseSearchCalls);
        Assert.Contains(plan.Warnings, w => w.Category == "AlbumAlreadyApproved");
    }

    [Fact]
    public void ComputeComposerHint_SingleComposer_AcrossEveryTrack_ReturnsThatComposer()
    {
        // The dominant classical case: every track has the same composer.
        // Top-3 reduces to a single entry.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Brahms Symphonies", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "Mvt 1", composer: "Brahms, Johannes (1833-1897)"),
                Track(2, "Mvt 2", composer: "Brahms, Johannes (1833-1897)"),
                Track(3, "Mvt 3", composer: "Brahms, Johannes (1833-1897)"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeComposerHint(group);
        Assert.Equal("Brahms, Johannes", hint);
    }

    [Fact]
    public void ComputeComposerHint_ThreeComposersEqualFrequency_ReturnsAllThree()
    {
        // "String Quartets: Beethoven, Schubert, Brahms" pattern.
        // All three composers appear; top-3 cap accommodates the full set.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "String Quartets", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "T1", composer: "Beethoven, Ludwig van (1770-1827)"),
                Track(2, "T2", composer: "Schubert, Franz (1797-1828)"),
                Track(3, "T3", composer: "Brahms, Johannes (1833-1897)"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeComposerHint(group);
        Assert.Contains("Beethoven", hint);
        Assert.Contains("Schubert", hint);
        Assert.Contains("Brahms", hint);
    }

    [Fact]
    public void ComputeComposerHint_DominantPlusLongTail_TakesTop3ByFrequency()
    {
        // "100 Most Famous Classical": one composer dominates (say 10 tracks),
        // a long tail of one-off appearances. The cap of 3 picks the top
        // contributors, dropping the diluting long tail.
        var tracks = new List<ItunesTrack>();
        // 10 Beethoven tracks
        for (int i = 1; i <= 10; i++)
            tracks.Add(Track(i, $"T{i}", composer: "Beethoven, Ludwig van (1770-1827)"));
        // 3 Mozart tracks
        for (int i = 11; i <= 13; i++)
            tracks.Add(Track(i, $"T{i}", composer: "Mozart, Wolfgang Amadeus (1756-1791)"));
        // 2 Brahms tracks
        for (int i = 14; i <= 15; i++)
            tracks.Add(Track(i, $"T{i}", composer: "Brahms, Johannes (1833-1897)"));
        // 1 each of 5 different composers (the dropped long tail)
        tracks.Add(Track(16, "T16", composer: "Chopin, Frédéric (1810-1849)"));
        tracks.Add(Track(17, "T17", composer: "Liszt, Franz (1811-1886)"));
        tracks.Add(Track(18, "T18", composer: "Wagner, Richard (1813-1883)"));
        tracks.Add(Track(19, "T19", composer: "Mahler, Gustav (1860-1911)"));
        tracks.Add(Track(20, "T20", composer: "Debussy, Claude (1862-1918)"));

        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Classical Hits", AlbumArtist: null, Tracks: tracks);

        var hint = ItunesImportEnrichmentPlanner.ComputeComposerHint(group);
        // Top-3 by frequency: Beethoven (10), Mozart (3), Brahms (2).
        Assert.Contains("Beethoven", hint);
        Assert.Contains("Mozart", hint);
        Assert.Contains("Brahms", hint);
        // Long tail dropped.
        Assert.DoesNotContain("Chopin", hint);
        Assert.DoesNotContain("Liszt", hint);
    }

    [Fact]
    public void ComputeComposerHint_VariousSentinel_Filtered()
    {
        // The "(Various)" sentinel composer for multi-composer collaborative
        // pieces shouldn't dilute the hint with a useless query term.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Compilation", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "T1", composer: "(Various)"),
                Track(2, "T2", composer: "Various"),
                Track(3, "T3", composer: "Brahms, Johannes (1833-1897)"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeComposerHint(group);
        Assert.Equal("Brahms, Johannes", hint);
    }

    [Fact]
    public void ComputeComposerHint_AllNullOrUnparseable_ReturnsEmpty()
    {
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Mystery Album", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "T1", composer: null),
                Track(2, "T2", composer: ""),
                Track(3, "T3", composer: "   "),
            });

        Assert.Equal("", ItunesImportEnrichmentPlanner.ComputeComposerHint(group));
    }

    [Fact]
    public async Task AlbumPass_ComposerHint_ConcatenatedWithPerformerHint()
    {
        // Integration: the search call receives BOTH the composer surnames AND
        // the shared performer names in the artist hint, separated by spaces.
        // The enricher tokenises both into artist: terms.
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", composer: "Brahms, Johannes (1833-1897)",
                  album: "Brahms Symphonies", albumArtist: "",
                  artist: "Boston Symphony Orchestra, Andris Nelsons"),
            Track(2, "T2", composer: "Brahms, Johannes (1833-1897)",
                  album: "Brahms Symphonies", albumArtist: "",
                  artist: "Boston Symphony Orchestra, Andris Nelsons"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        var (_, artist) = Assert.Single(fake.ReleaseSearchCalls);
        // Composer hint first, then performer hint, both space-joined.
        Assert.Contains("Brahms",   artist);
        Assert.Contains("Johannes", artist);
        Assert.Contains("Boston Symphony Orchestra Andris Nelsons", artist);
    }

    [Fact]
    public async Task AlbumPass_CompilationWithMultipleComposers_StillGetsComposerHint()
    {
        // The case the user specifically wanted to support: compilation
        // album where the performer intersection is empty but the
        // composer hint still narrows MB's search via the top-3 composers.
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", composer: "Beethoven (1770-1827)",
                  album: "String Quartets Collection", albumArtist: "",
                  artist: "Quartet A"),
            Track(2, "T2", composer: "Schubert (1797-1828)",
                  album: "String Quartets Collection", albumArtist: "",
                  artist: "Quartet B"),
            Track(3, "T3", composer: "Brahms (1833-1897)",
                  album: "String Quartets Collection", albumArtist: "",
                  artist: "Quartet C"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        var (_, artist) = Assert.Single(fake.ReleaseSearchCalls);
        // Composer hint includes all three; performer intersection is empty
        // (different performers per track) so the artist hint comes entirely
        // from the composer side.
        Assert.Contains("Beethoven", artist);
        Assert.Contains("Schubert",  artist);
        Assert.Contains("Brahms",    artist);
    }

    [Fact]
    public void ComputeSharedPerformerHint_AllTracksSamePerformers_ReturnsShared()
    {
        // The Brahms Nelsons case: every track lists "Boston Symphony
        // Orchestra, Andris Nelsons" — the intersection equals both
        // performers, joined as a single hint string for MB's artist field.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Brahms Symphonies Nelsons", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "Mvt 1", artist: "Boston Symphony Orchestra, Andris Nelsons"),
                Track(2, "Mvt 2", artist: "Boston Symphony Orchestra, Andris Nelsons"),
                Track(3, "Mvt 3", artist: "Boston Symphony Orchestra, Andris Nelsons"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeSharedPerformerHint(group);

        Assert.Equal("Boston Symphony Orchestra Andris Nelsons", hint);
    }

    [Fact]
    public void ComputeSharedPerformerHint_PartialOverlap_KeepsOnlyShared()
    {
        // Opera-style album: every track has the conductor + orchestra,
        // but per-scene soloists differ. The intersection is the conductor
        // + orchestra — a useful narrowing signal even though it doesn't
        // include the soloists.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "Verdi Otello", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "Sc 1", artist: "La Scala, Karajan, Domingo"),
                Track(2, "Sc 2", artist: "La Scala, Karajan, Freni"),
                Track(3, "Sc 3", artist: "La Scala, Karajan, Domingo, Freni"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeSharedPerformerHint(group);

        Assert.Equal("La Scala Karajan", hint);
    }

    [Fact]
    public void ComputeSharedPerformerHint_NoOverlap_ReturnsEmpty()
    {
        // A compilation where every track has different performers.
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "100 Best Classical", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "T1", artist: "Karajan"),
                Track(2, "T2", artist: "Bernstein"),
                Track(3, "T3", artist: "Solti"),
            });

        var hint = ItunesImportEnrichmentPlanner.ComputeSharedPerformerHint(group);

        Assert.Equal("", hint);
    }

    [Fact]
    public void ComputeSharedPerformerHint_MissingArtistField_ReturnsEmpty()
    {
        var group = new ItunesImportEnrichmentPlanner.AlbumGroup(
            Key: "k", Title: "X", AlbumArtist: null,
            Tracks: new List<ItunesTrack>
            {
                Track(1, "T1", artist: null),
                Track(2, "T2", artist: null),
            });

        Assert.Equal("", ItunesImportEnrichmentPlanner.ComputeSharedPerformerHint(group));
    }

    [Fact]
    public async Task AlbumPass_EmptyAlbumArtist_UsesSharedPerformersAsArtistHint()
    {
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        // Brahms-Nelsons scenario: AlbumArtist is empty, but every track
        // carries the same Artist field.
        var tracks = new[]
        {
            Track(1, "Mvt 1", composer: "Brahms (1833-1897)",
                  album: "Brahms Symphonies Nelsons", albumArtist: "",
                  artist: "Boston Symphony Orchestra, Andris Nelsons"),
            Track(2, "Mvt 2", composer: "Brahms (1833-1897)",
                  album: "Brahms Symphonies Nelsons", albumArtist: "",
                  artist: "Boston Symphony Orchestra, Andris Nelsons"),
            Track(3, "Mvt 3", composer: "Brahms (1833-1897)",
                  album: "Brahms Symphonies Nelsons", albumArtist: "",
                  artist: "Boston Symphony Orchestra, Andris Nelsons"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        // The planner combines the composer hint (single shared composer
        // → "Brahms") with the shared-performer hint, so the artist hint
        // carries both. The enricher tokenises into artist:Brahms +
        // artist:Boston artist:Symphony artist:Orchestra artist:Andris
        // artist:Nelsons in the Lucene query.
        var (_, artist) = Assert.Single(fake.ReleaseSearchCalls);
        Assert.Contains("Brahms",                                  artist);
        Assert.Contains("Boston Symphony Orchestra Andris Nelsons", artist);
    }

    [Fact]
    public async Task AlbumPass_NonEmptyAlbumArtist_TakesPrecedenceOverSharedArtist()
    {
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        // AlbumArtist is set → it wins, even if track Artist fields would
        // produce a different intersection. The user's curated AlbumArtist
        // is more authoritative than the per-track field.
        var tracks = new[]
        {
            Track(1, "T1", composer: "Brahms (1833-1897)",
                  album: "Symphonies", albumArtist: "Boston Symphony",
                  artist: "Different Artist"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        var (_, artist) = Assert.Single(fake.ReleaseSearchCalls);
        // AlbumArtist wins over the per-track intersection. The composer
        // hint ("Brahms") still gets prepended.
        Assert.Contains("Brahms",         artist);
        Assert.Contains("Boston Symphony", artist);
    }

    [Fact]
    public async Task MbReleaseId_OnTrack_UsesMbidShortcut_NotSearch()
    {
        // Slice 6: when an iTunes track carries an MB Release ID (Picard /
        // similar pre-tagged), the planner calls GetReleaseByMbidAsync
        // directly instead of SearchReleasesAsync.
        var fake = new FakeEnricher
        {
            GetReleaseByMbidReturns = _ =>
                new MbReleaseCandidate(
                    MbReleaseId: "release-001",
                    Title: "MB Title", Label: "DG", CatalogueNumber: "111",
                    Barcode: null, Date: null, Country: null,
                    DiscCount: 1, TrackCount: 1,
                    Tracks: Array.Empty<MbReleaseTrack>(),
                    RecordingEvents: Array.Empty<MbReleaseEvent>(),
                    Credits: Array.Empty<MbReleaseCredit>(),
                    Confidence: 1.0),
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", album: "A", albumArtist: "B",
                  mbReleaseId: "release-001"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        Assert.Single(fake.ReleaseByMbidCalls);
        Assert.Equal("release-001", fake.ReleaseByMbidCalls[0]);
        Assert.Empty(fake.ReleaseSearchCalls);
        // The candidate from the shortcut path flows through to the proposal.
        var album = Assert.Single(plan.Albums);
        Assert.Equal("release-001", album.Candidates[0].MbReleaseId);
    }

    [Fact]
    public async Task NoMbReleaseId_FallsBackToSearch()
    {
        // Pin the inverse: no MBID → search path. Already covered indirectly
        // by other album-pass tests but worth an explicit assertion next to
        // the MBID-shortcut case.
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", album: "A", albumArtist: "B"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        Assert.Empty(fake.ReleaseByMbidCalls);
        Assert.Single(fake.ReleaseSearchCalls);
    }

    [Fact]
    public async Task ProvisionalCanonAlbum_StillTriggersMbSearch()
    {
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var existing = new[]
        {
            new CanonAlbum { Title = "Album A", IsProvisional = true },
        };
        var tracks = new[]
        {
            Track(1, "T1", album: "Album A", albumArtist: ""),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            existing, dotInterpretations: null, progress: null);

        Assert.Single(plan.Albums);
        Assert.Single(fake.ReleaseSearchCalls);
    }

    // ── Artist pass ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ArtistPass_SkippedEntirely_NoCallsRegardlessOfTrackComposers()
    {
        // The artist pass is no longer run — even with multiple distinct
        // composers across tracks, the enricher's ResolveArtistAsync is
        // never invoked.
        var fake = new FakeEnricher
        {
            ResolveArtistReturns = (_, _) => new[] { Artist("a1", 0.95) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", composer: "Beethoven (1770-1827)", album: "A"),
            Track(2, "T2", composer: "Beethoven (1770-1827)", album: "A"),
            Track(3, "T3", composer: "Mozart (1756-1791)",    album: "B"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        Assert.Empty(plan.Artists);
        Assert.Empty(fake.ArtistCalls);
    }

    [Fact]
    public async Task ApprovedComposerWithBirthYear_SkippedFromArtistPass()
    {
        var fake = new FakeEnricher();
        var planner = new ItunesImportEnrichmentPlanner(fake);

        // Approved canon composer with a BirthYear → "fully populated".
        var existing = new[]
        {
            new CanonComposer
            {
                Name = "Beethoven",
                SortName = "Beethoven",
                BirthDate = "1770-12-17",
                IsProvisional = false,
            },
        };
        var tracks = new[]
        {
            Track(1, "T1", composer: "Beethoven (1770-1827)", album: "A"),
        };

        var plan = await planner.PlanAsync(
            tracks, existing, Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        Assert.Empty(plan.Artists);
        Assert.Empty(fake.ArtistCalls);
    }

    // ApprovedComposerWithoutBirthYear_StillProposed retired — artist pass
    // no longer runs, the composer is not surfaced regardless of canon state.

    [Fact]
    public void SplitComposerNameForQuery_SurnameFirstNameForm()
    {
        var (last, first) = ItunesImportEnrichmentPlanner.SplitComposerNameForQuery(
            "Beethoven, Ludwig van");
        Assert.Equal("Beethoven", last);
        Assert.Equal("Ludwig van", first);

        var (lastBare, firstBare) = ItunesImportEnrichmentPlanner.SplitComposerNameForQuery(
            "Beethoven");
        Assert.Equal("Beethoven", lastBare);
        Assert.Null(firstBare);
    }

    // ── Work pass ────────────────────────────────────────────────────────────

    [Fact]
    public async Task WorkPass_SkippedEntirely_NoCallsRegardlessOfTrackComposition()
    {
        // Work pass retired; ResolveWorkAsync is never invoked, regardless
        // of how many tracks would have grouped into pieces.
        var fake = new FakeEnricher
        {
            ResolveWorkReturns = (_, _) => new[] { Work("w1", 0.9, movementCount: 3) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "Piano Sonata - 1. Adagio",  composer: "Beethoven (1770-1827)", album: "A"),
            Track(2, "Piano Sonata - 2. Allegro", composer: "Beethoven (1770-1827)", album: "A"),
            Track(3, "Piano Sonata - 3. Presto",  composer: "Beethoven (1770-1827)", album: "A"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        Assert.Empty(plan.Works);
        Assert.Empty(fake.WorkCalls);
    }

    // ── PreferredIndex + warnings ────────────────────────────────────────────

    [Fact]
    public async Task PreferredIndex_PointsAtTopRankedAlbumCandidate()
    {
        var fake = new FakeEnricher
        {
            // Two album candidates; enricher already returns them
            // confidence-sorted in slice 2, planner preserves that order.
            SearchReleasesReturns = (_, _) => new[]
            {
                Release("strong", 0.92),
                Release("weak",   0.55),
            },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var tracks = new[]
        {
            Track(1, "T1", composer: "X (1900-1970)", album: "A"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null);

        var album = Assert.Single(plan.Albums);
        Assert.Equal(0, album.PreferredIndex);
        Assert.Equal("strong", album.Candidates[0].MbReleaseId);

        // Artist/work passes retired → those proposal lists stay empty.
        Assert.Empty(plan.Artists);
        Assert.Empty(plan.Works);
    }

    // ── Progress + cancellation ──────────────────────────────────────────────

    /// <summary>
    /// Synchronous <see cref="IProgress{T}"/> — records every report on the
    /// caller's thread. <c>Progress&lt;T&gt;</c> would marshal callbacks
    /// through <c>SynchronizationContext</c> (or a thread-pool work item
    /// when no context is captured), so the events would arrive
    /// asynchronously and the test would race. Implementing
    /// <see cref="IProgress{T}"/> directly bypasses the marshal.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = new();
        public void Report(T value) => Reports.Add(value);
    }

    [Fact]
    public async Task Progress_FiresPerResolvedAlbum_ArtistAndWorkCountsStayZero()
    {
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        var progress = new SyncProgress<EnrichmentProgress>();

        var tracks = new[]
        {
            Track(1, "Piece - 1. Mvt", composer: "X (1900-1970)", album: "A"),
        };

        await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: progress);

        Assert.NotEmpty(progress.Reports);
        // Album pass progresses; artist + work totals stay zero (passes
        // retired).
        var last = progress.Reports[^1];
        Assert.Equal(1, last.AlbumsTotal);
        Assert.Equal(last.AlbumsTotal,  last.AlbumsResolved);
        Assert.Equal(0, last.ArtistsTotal);
        Assert.Equal(0, last.WorksTotal);
    }

    [Fact]
    public async Task Cancellation_BeforeAnyResolution_ReturnsEmptyPlanWithCancelWarning()
    {
        // Slice 5: the planner is cancel-tolerant. A cancelled token returns
        // an EnrichmentPlan with whatever was resolved so far + a Cancelled
        // warning. The review pane uses this to let the user Apply with the
        // partial set instead of forcing a re-run.
        var fake = new FakeEnricher
        {
            SearchReleasesReturns = (_, _) => new[] { Release("r1", 0.9) },
        };
        var planner = new ItunesImportEnrichmentPlanner(fake);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tracks = new[]
        {
            Track(1, "T1", composer: "X (1900-1970)", album: "A"),
        };

        var plan = await planner.PlanAsync(
            tracks, Array.Empty<CanonComposer>(), Array.Empty<CanonPiece>(),
            Array.Empty<CanonAlbum>(), dotInterpretations: null, progress: null,
            ct: cts.Token);

        Assert.Empty(plan.Albums);
        Assert.Empty(plan.Artists);
        Assert.Empty(plan.Works);
        Assert.Contains(plan.Warnings, w => w.Category == "Cancelled");
    }
}
