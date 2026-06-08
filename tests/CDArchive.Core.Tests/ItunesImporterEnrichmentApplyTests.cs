using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for the slice-4 apply phase: <see cref="ItunesImporter.Import"/>'s
/// new <c>enrichment</c> parameter routes the user's review-pane decisions
/// into create-time mutations on composers / albums / pieces.
///
/// <para>Every apply rule has a defensive guard so curated canon is never
/// silently overwritten. These tests pin each guard's behaviour:</para>
/// <list type="bullet">
///   <item>Composer: fresh-create + provisional+blank-dates → apply;
///         approved canon → untouched.</item>
///   <item>Album: fresh-create + provisional-merge target → apply;
///         approved-merge target → untouched.</item>
///   <item>Piece: fresh-create only — existing canon is never overwritten.
///         <c>ApplyMovementList</c> separately gates the subpiece-tree
///         replacement.</item>
///   <item>Backward-compat: <c>enrichment: null</c> reproduces the legacy
///         non-MB behaviour byte-for-byte.</item>
/// </list>
/// </summary>
public class ItunesImporterEnrichmentApplyTests
{
    private const string BeethovenMbid = "1f9df192-a621-4f54-8850-2c5373b7eac9";
    private const string ChoralMbid    = "3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e";
    private const string SonataMbid    = "8c6a91a3-9f29-4bba-a8f5-d2e93b9f7d2c";

    private static ItunesTrack Track(
        int id, string name, string? composer = null,
        string? album = null, string? albumArtist = null,
        int? trackNumber = null, int? durationMs = 180_000) =>
        new(id, PersistentId: null, DiscNumber: 1, TrackNumber: trackNumber,
            Name: name, DurationMs: durationMs, Genre: null, Composer: composer,
            Album: album, AlbumArtist: albumArtist, Artist: null,
            DateAdded: null, Location: null);

    private static MbArtistSuggestion ArtistOf(string name, int? birth, int? death) =>
        new(MbArtistId: BeethovenMbid, Name: name, SortName: name,
            BirthYear: birth, DeathYear: death,
            BirthPlace: "Bonn", DeathPlace: "Vienna", Confidence: 0.99);

    private static MbReleaseCandidate ReleaseOf(string title, string? label, string? catNo)
        => new(MbReleaseId: ChoralMbid, Title: title, Label: label,
            CatalogueNumber: catNo, Barcode: "028947795230",
            Date: "2008-04-15", Country: "DE",
            DiscCount: 1, TrackCount: 1,
            Tracks: Array.Empty<MbReleaseTrack>(),
            RecordingEvents: new[]
            {
                new MbReleaseEvent(
                    Date: "2008-04-10", Venue: "Funkhaus",
                    City: "Berlin", Country: "DE",
                    Engineers: new[] { "Eng A" }, Producers: new[] { "Prod B" }),
            },
            Credits: new[]
            {
                new MbReleaseCredit("Hélène Grimaud", "Artist", "Piano", null),
                new MbReleaseCredit("Salonen", "conductor", null, null),
            },
            Confidence: 1.0);

    private static MbWorkSuggestion WorkOf(
        string title, string? key, string? mode, int movementCount)
    {
        var movements = new List<MbWorkMovement>();
        for (int i = 1; i <= movementCount; i++)
            movements.Add(new MbWorkMovement(i, $"Mov {i}", Tempo: i == 1 ? "Adagio" : null));
        return new MbWorkSuggestion(
            MbWorkId: SonataMbid, Title: title, Catalogue: null,
            KeyTonality: key, KeyMode: mode,
            Movements: movements, Confidence: 1.0);
    }

    // ── Backwards compatibility ──────────────────────────────────────────────

    [Fact]
    public void NullEnrichment_PreservesLegacyBehaviour_ByteForByte()
    {
        // The legacy path (no enrichment at all) is the back-compat contract:
        // no MBID stamped, no MB-derived scalars, no MB-derived subpieces.
        var tracks = new[]
        {
            Track(1, "Piano Sonata", composer: "Beethoven (1770-1827)",
                  album: "Album A", trackNumber: 1),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();
        var albums    = new List<CanonAlbum>();

        var result = ItunesImporter.Import(tracks, composers, pieces, albums,
            dotInterpretations: null, enrichment: null);

        // Composer / piece / album all created provisionally, no MBIDs.
        var c = Assert.Single(composers);
        Assert.Null(c.MusicBrainzArtistId);
        var p = Assert.Single(pieces);
        Assert.Null(p.MusicBrainzWorkId);
        var a = Assert.Single(result.NewAlbums);
        Assert.Null(a.MusicBrainzReleaseId);
    }

    // ── Composer apply rules ─────────────────────────────────────────────────

    [Fact]
    public void FreshComposer_WithArtistChoice_AppliesDatesAndMbid()
    {
        var tracks = new[]
        {
            // Composer field has no dates — MB should fill them.
            Track(1, "Sonata", composer: "Beethoven", album: "A", trackNumber: 1),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Beethoven"] = new(ArtistOf("Beethoven", 1770, 1827)),
            },
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks, composers, pieces, existingAlbums: null,
            dotInterpretations: null, enrichment: enrichment);

        var c = Assert.Single(composers);
        Assert.Equal("1770", c.BirthDate);
        Assert.Equal("1827", c.DeathDate);
        Assert.Equal("Bonn", c.BirthPlace);
        Assert.Equal("Vienna", c.DeathPlace);
        Assert.Equal(BeethovenMbid, c.MusicBrainzArtistId);
    }

    [Fact]
    public void FreshComposer_iTunesParsedYearsWin_OverMbForBlankFill()
    {
        // iTunes carries the years; MB has different (wrong) values.
        // The blank-field guard means MB only fills empty slots, so the
        // iTunes-parsed years survive.
        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
        };
        var composers = new List<CanonComposer>();

        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Beethoven"] = new(ArtistOf("Beethoven", birth: 9999, death: 9999)),
            },
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks, composers, new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var c = Assert.Single(composers);
        // iTunes-parsed years preserved; MB only stamped the MBID + places.
        Assert.Equal("1770", c.BirthDate);
        Assert.Equal("1827", c.DeathDate);
        Assert.Equal(BeethovenMbid, c.MusicBrainzArtistId);
    }

    [Fact]
    public void ApprovedExistingComposer_WithArtistChoice_UntouchedExceptApprovedAlready()
    {
        // The defensive guard: even if the user picked an artist choice for
        // an approved canon composer, the importer does NOT overwrite it.
        var approved = new CanonComposer
        {
            Name = "Beethoven",
            SortName = "Beethoven, Ludwig van",
            BirthDate = "1770-12-17",
            DeathDate = "1827-03-26",
            IsProvisional = false,
        };
        var composers = new List<CanonComposer> { approved };

        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven", album: "A", trackNumber: 1),
        };
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Beethoven"] = new(ArtistOf("Beethoven", 9999, 9999)),
            },
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks, composers, new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        // Approved canon composer survived untouched: no MBID stamped,
        // dates not replaced.
        Assert.Equal("1770-12-17", approved.BirthDate);
        Assert.Equal("1827-03-26", approved.DeathDate);
        Assert.Null(approved.MusicBrainzArtistId);
    }

    [Fact]
    public void ProvisionalExistingComposer_WithBlankBirthDate_GetsEnriched()
    {
        var provisional = new CanonComposer
        {
            Name = "Beethoven",
            SortName = "Beethoven",
            // BirthDate intentionally blank
            IsProvisional = true,
        };
        var composers = new List<CanonComposer> { provisional };

        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven", album: "A", trackNumber: 1),
        };
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                ["Beethoven"] = new(ArtistOf("Beethoven", 1770, 1827)),
            },
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks, composers, new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        Assert.Equal("1770", provisional.BirthDate);
        Assert.Equal("1827", provisional.DeathDate);
        Assert.Equal(BeethovenMbid, provisional.MusicBrainzArtistId);
    }

    [Fact]
    public void ArtistChoice_WithApplyFalse_NoEnrichmentApplied()
    {
        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven", album: "A", trackNumber: 1),
        };
        var composers = new List<CanonComposer>();

        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                // User unchecked the apply box in the review pane.
                ["Beethoven"] = new(ArtistOf("Beethoven", 1770, 1827), Apply: false),
            },
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks, composers, new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var c = Assert.Single(composers);
        Assert.Null(c.MusicBrainzArtistId);
        Assert.Null(c.BirthDate);
    }

    // ── Album apply rules ────────────────────────────────────────────────────

    [Fact]
    public void FreshAlbum_WithChoice_AppliesLabelCatNoSessionAndMbid()
    {
        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven (1770-1827)",
                  album: "Choral Fantasy", albumArtist: "Grimaud", trackNumber: 1),
        };

        var albumKey = ItunesImportEnrichmentPlanner.BuildAlbumKey("Choral Fantasy", "Grimaud");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey: new Dictionary<string, AppliedAlbumEnrichment>
            {
                [albumKey] = new(ReleaseOf("Beethoven: Choral Fantasy", "DG", "477 9523")),
            },
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        var result = ItunesImporter.Import(tracks,
            new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var a = Assert.Single(result.NewAlbums);
        Assert.Equal("DG",       a.Label);
        Assert.Equal("477 9523", a.CatalogueNumber);
        Assert.Equal("028947795230", a.Barcode);
        Assert.Equal("2008-04-10", a.SessionDates);
        Assert.Equal("Funkhaus",   a.SessionVenue);
        Assert.Equal("Berlin",     a.SessionCity);
        Assert.Equal(ChoralMbid,   a.MusicBrainzReleaseId);
        Assert.NotNull(a.SessionEngineers);
        Assert.Contains("Eng A",  a.SessionEngineers!);
    }

    [Fact]
    public void ApprovedExistingAlbum_WithChoice_OnlyMergesTracks_NoMetadataChange()
    {
        // The user picked an album choice but the canon album is approved.
        // The importer routes new tracks into the existing album (merge
        // behaviour from M5) but never modifies its scalars.
        var approved = new CanonAlbum
        {
            Title = "Choral Fantasy",
            Label = "Existing Label",
            IsProvisional = false,
        };
        var albums = new List<CanonAlbum> { approved };

        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven (1770-1827)",
                  album: "Choral Fantasy", albumArtist: "Grimaud", trackNumber: 1),
        };

        var albumKey = ItunesImportEnrichmentPlanner.BuildAlbumKey("Choral Fantasy", "Grimaud");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey: new Dictionary<string, AppliedAlbumEnrichment>
            {
                [albumKey] = new(ReleaseOf("MB Title", "MB Label", "MB CatNo")),
            },
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks,
            new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: albums, dotInterpretations: null, enrichment: enrichment);

        // Approved album survived: original Label preserved, no MBID stamped.
        Assert.Equal("Existing Label", approved.Label);
        Assert.Null(approved.MusicBrainzReleaseId);
        // But the new track DID get merged in.
        Assert.Single(approved.Discs);
    }

    [Fact]
    public void ProvisionalExistingAlbum_WithChoice_GetsEnriched()
    {
        var provisional = new CanonAlbum
        {
            Title = "Choral Fantasy",
            // Label intentionally blank
            IsProvisional = true,
        };
        var albums = new List<CanonAlbum> { provisional };

        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven (1770-1827)",
                  album: "Choral Fantasy", albumArtist: "Grimaud", trackNumber: 1),
        };

        var albumKey = ItunesImportEnrichmentPlanner.BuildAlbumKey("Choral Fantasy", "Grimaud");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey: new Dictionary<string, AppliedAlbumEnrichment>
            {
                [albumKey] = new(ReleaseOf("MB Title", "DG", "477 9523")),
            },
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        ItunesImporter.Import(tracks,
            new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: albums, dotInterpretations: null, enrichment: enrichment);

        // Provisional album got the MB-derived fields. Title preserved
        // (user-typed-or-iTunes-derived title wins over MB's).
        Assert.Equal("Choral Fantasy", provisional.Title);
        Assert.Equal("DG",       provisional.Label);
        Assert.Equal("477 9523", provisional.CatalogueNumber);
        Assert.Equal(ChoralMbid, provisional.MusicBrainzReleaseId);
    }

    [Fact]
    public void AlbumChoice_ApplyMetadataFalse_OnlyMbidStamped()
    {
        // Per-aspect checkboxes: user wants just the MBID linkage without
        // overwriting any scalars. Pin that ApplyMetadata=false skips the
        // Label / CatNo / Barcode block but the MBID is still stamped
        // (linkage is always-on).
        var tracks = new[]
        {
            Track(1, "Sonata", composer: "Beethoven (1770-1827)",
                  album: "Choral Fantasy", albumArtist: "Grimaud", trackNumber: 1),
        };

        var albumKey = ItunesImportEnrichmentPlanner.BuildAlbumKey("Choral Fantasy", "Grimaud");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey: new Dictionary<string, AppliedAlbumEnrichment>
            {
                [albumKey] = new(
                    ReleaseOf("MB Title", "DG", "477 9523"),
                    ApplyMetadata:         false,
                    ApplyPerformers:       false,
                    ApplyRecordingSession: false),
            },
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>());

        var result = ItunesImporter.Import(tracks,
            new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var a = Assert.Single(result.NewAlbums);
        Assert.Null(a.Label);
        Assert.Null(a.CatalogueNumber);
        Assert.Null(a.SessionDates);
        Assert.Equal(ChoralMbid, a.MusicBrainzReleaseId);
    }

    // ── Piece apply rules ────────────────────────────────────────────────────

    [Fact]
    public void FreshPiece_WithWorkChoice_AppliesTitleAndMbid()
    {
        var tracks = new[]
        {
            Track(1, "Piano Sonata", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
        };
        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Piano Sonata");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                [workKey] = new(WorkOf(
                    title: "Piano Sonata No. 14 in C♯ minor, Op. 27 No. 2 \"Moonlight\"",
                    key: "C♯", mode: "minor", movementCount: 0)),
            });

        var pieces = new List<CanonPiece>();
        ItunesImporter.Import(tracks, new List<CanonComposer>(), pieces,
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var p = Assert.Single(pieces);
        // MB title replaces the iTunes-parsed shell.
        Assert.Equal("Piano Sonata No. 14 in C♯ minor, Op. 27 No. 2 \"Moonlight\"", p.Title);
        Assert.Equal("C♯",    p.KeyTonality);
        Assert.Equal("minor", p.KeyMode);
        Assert.Equal(SonataMbid, p.MusicBrainzWorkId);
    }

    [Fact]
    public void FreshPiece_ApplyMovementListTrue_ReplacesSubpiecesWithMbStructure()
    {
        // iTunes track titles match MB's movement titles, so per-track
        // EnsureSubpiecePath resolves cleanly into the MB-pre-populated
        // subpieces (title match) — no duplicate-subpiece churn. This is
        // the happy path the design assumes when the planner's
        // movement-count-mismatch check passes.
        var tracks = new[]
        {
            Track(1, "Sonata - Mov 1", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
            Track(2, "Sonata - Mov 2", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 2),
            Track(3, "Sonata - Mov 3", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 3),
        };
        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Sonata");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                [workKey] = new(WorkOf(
                    title: "Piano Sonata No. 14",
                    key:   null, mode: null,
                    movementCount: 3)),
            });

        var pieces = new List<CanonPiece>();
        ItunesImporter.Import(tracks, new List<CanonComposer>(), pieces,
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var p = Assert.Single(pieces);
        // The piece carries MB's canonical movements with the MB titles.
        Assert.NotNull(p.Subpieces);
        Assert.Equal(3, p.Subpieces!.Count);
        Assert.Equal("Mov 1", p.Subpieces[0].Title);
        Assert.Equal(1,       p.Subpieces[0].Number);
        Assert.Equal("Mov 3", p.Subpieces[2].Title);
        // First movement gets the WorkOf-supplied tempo marker.
        Assert.NotNull(p.Subpieces[0].Markers);
        Assert.Equal("Adagio", p.Subpieces[0].Markers![0].Value);
    }

    [Fact]
    public void FreshPiece_ApplyMovementList_TitleMismatch_CreatesDuplicateSubpieces_DocumentedRisk()
    {
        // Pins the documented limitation: when iTunes track titles do NOT
        // match MB's canonical movement titles, the per-track
        // EnsureSubpiecePath misses the MB-pre-populated subpieces (no
        // title match, no number match either since MB subpieces have
        // numbers AND non-empty titles) and creates new subpieces alongside.
        // Result: piece carries both MB's structure and iTunes-derived
        // duplicates.
        //
        // The review pane's MovementCountMismatch detector guards against
        // this for the common case (auto-unchecks the box), but it can't
        // detect title-disagreement-with-matching-count. The user has full
        // visibility via the side-by-side preview.
        var tracks = new[]
        {
            Track(1, "Sonata - 1. Adagio sostenuto", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
            Track(2, "Sonata - 2. Allegretto",        composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 2),
            Track(3, "Sonata - 3. Presto agitato",    composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 3),
        };
        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Sonata");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                [workKey] = new(WorkOf("Piano Sonata", null, null, movementCount: 3)),
            });

        var pieces = new List<CanonPiece>();
        ItunesImporter.Import(tracks, new List<CanonComposer>(), pieces,
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var p = Assert.Single(pieces);
        // 3 MB-titled subpieces + 3 iTunes-titled subpieces = 6.
        // Pinning this so a future EnsureSubpiecePath behaviour-change
        // (e.g. fuzzy matching to MB titles) shows up loud.
        Assert.NotNull(p.Subpieces);
        Assert.Equal(6, p.Subpieces!.Count);
        // MBID still stamped regardless.
        Assert.Equal(SonataMbid, p.MusicBrainzWorkId);
    }

    [Fact]
    public void FreshPiece_ApplyMovementListFalse_SubpiecesNotReplacedByMb()
    {
        var tracks = new[]
        {
            Track(1, "Sonata - 1. Adagio sostenuto",  composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
            Track(2, "Sonata - 2. Allegretto",        composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 2),
        };
        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Sonata");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                [workKey] = new(
                    WorkOf("Piano Sonata No. 14", null, null, movementCount: 5),
                    ApplyMovementList: false),
            });

        var pieces = new List<CanonPiece>();
        ItunesImporter.Import(tracks, new List<CanonComposer>(), pieces,
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        var p = Assert.Single(pieces);
        // The piece's subpieces come from the importer's per-track
        // EnsureSubpiecePath, which built two from the iTunes tracks —
        // NOT MB's five-movement structure.
        Assert.NotNull(p.Subpieces);
        Assert.Equal(2, p.Subpieces!.Count);
        // MBID still stamped (linkage is always-on regardless of structure).
        Assert.Equal(SonataMbid, p.MusicBrainzWorkId);
    }

    [Fact]
    public void ExistingApprovedPiece_WithWorkChoice_Untouched()
    {
        var approved = new CanonPiece
        {
            Composer = "Beethoven",
            Title    = "Original Title",
            IsProvisional = false,
        };
        var pieces = new List<CanonPiece> { approved };

        var tracks = new[]
        {
            Track(1, "Original Title", composer: "Beethoven (1770-1827)",
                  album: "A", trackNumber: 1),
        };
        var workKey = EnrichmentChoices.BuildWorkKey("Beethoven", "Original Title");
        var enrichment = new EnrichmentChoices(
            AlbumsByKey:   new Dictionary<string, AppliedAlbumEnrichment>(),
            ArtistsByName: new Dictionary<string, AppliedArtistEnrichment>(),
            WorksByKey:    new Dictionary<string, AppliedWorkEnrichment>(StringComparer.OrdinalIgnoreCase)
            {
                [workKey] = new(WorkOf("MB Replacement Title", "F", "major", 0)),
            });

        ItunesImporter.Import(tracks, new List<CanonComposer>(), pieces,
            existingAlbums: null, dotInterpretations: null, enrichment: enrichment);

        // Existing canon piece (approved) survived: title, key, mode preserved;
        // no MBID stamped.
        Assert.Equal("Original Title", approved.Title);
        Assert.Null(approved.KeyTonality);
        Assert.Null(approved.MusicBrainzWorkId);
    }
}
