using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="ItunesImporter"/>. Focused on the edge cases that
/// produced data-loss bugs in the original importer: missing iTunes Album
/// field, missing iTunes TrackNumber, duplicate (disc, track#) tuples.
/// </summary>
public class ItunesImporterTests
{
    private static ItunesTrack Track(
        int trackId,
        string name,
        string? album = null,
        int? trackNumber = null,
        int? discNumber = null,
        string? composer = null)
        => new(
            TrackId:      trackId,
            PersistentId: null,
            DiscNumber:   discNumber,
            TrackNumber:  trackNumber,
            Name:         name,
            DurationMs:   180_000,
            Genre:        null,
            Composer:     composer,
            Album:        album,
            AlbumArtist:  null,
            Artist:       null,
            DateAdded:    DateTime.UtcNow,
            Location:     null);

    /// <summary>
    /// Tracks with no Album field become loose tracks (singletons, no album
    /// wrapping), not synthetic one-track albums. The composer / piece
    /// resolution path is identical to the album-bound case.
    /// </summary>
    [Fact]
    public void StandaloneTracks_BecomeLooseTracks()
    {
        var tracks = new[]
        {
            Track(1, "Souvenir d'une nuit d'été à Madrid", composer: "Glinka, Mikhail (1804-1857)", trackNumber: 1),
            Track(2, "An Outdoor Overture",                composer: "Copland, Aaron (1900-1990)",  trackNumber: 1),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var result = ItunesImporter.Import(tracks, composers, pieces);

        Assert.Empty(result.NewAlbums);
        Assert.Equal(2, result.NewLooseTracks.Count);

        // Each loose track has TrackNumber=0 (the loose sentinel), its iTunes
        // Name parsed into a piece-ref, and its composer registered. Both tracks
        // had a Composer field, so they're catalogued (PieceRefs set) rather
        // than uncatalogued (Description set).
        var glinkaTrack  = Assert.Single(result.NewLooseTracks,
            t => t.PieceRefs?[0].Composer == "Glinka, Mikhail");
        var coplandTrack = Assert.Single(result.NewLooseTracks,
            t => t.PieceRefs?[0].Composer == "Copland, Aaron");

        Assert.Equal(0, glinkaTrack.TrackNumber);
        Assert.Equal(0, coplandTrack.TrackNumber);
        Assert.Equal("Souvenir d'une nuit d'été à Madrid", glinkaTrack.PieceRefs![0].PieceTitle);
        Assert.Equal("An Outdoor Overture",                coplandTrack.PieceRefs![0].PieceTitle);
    }

    /// <summary>
    /// A standalone track with NO iTunes TrackNumber becomes a loose track
    /// with TrackNumber=0. There's no album-renumber path to worry about
    /// because loose tracks aren't position-indexed inside a disc.
    /// </summary>
    [Fact]
    public void StandaloneTrack_WithNullTrackNumber_BecomesLooseWithSentinelZero()
    {
        var tracks = new[]
        {
            Track(1, "Standalone Track", composer: "Glinka, Mikhail (1804-1857)", trackNumber: null),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        Assert.Empty(result.NewAlbums);
        var loose = Assert.Single(result.NewLooseTracks);
        Assert.Equal(0, loose.TrackNumber);
    }

    /// <summary>
    /// Albumless tracks should pick up their Artist field as track-level
    /// performers — there's no album-level common-set to dedupe against.
    /// </summary>
    [Fact]
    public void StandaloneTrack_WithArtist_PopulatesTrackLevelPerformers()
    {
        var tracks = new[]
        {
            new ItunesTrack(
                TrackId:      1,
                PersistentId: null,
                DiscNumber:   null,
                TrackNumber:  null,
                Name:         "Standalone",
                DurationMs:   180_000,
                Genre:        null,
                Composer:     "Glinka, Mikhail (1804-1857)",
                Album:        null,
                AlbumArtist:  null,
                Artist:       "Lang Lang, Studio recording",
                DateAdded:    DateTime.UtcNow,
                Location:     null),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var loose = Assert.Single(result.NewLooseTracks);
        Assert.NotNull(loose.Performers);
        Assert.Equal(2, loose.Performers!.Count);
        Assert.Equal("Lang Lang",        loose.Performers[0].Name);
        Assert.Equal("Studio recording", loose.Performers[1].Name);
    }

    /// <summary>
    /// A bona-fide album with duplicate iTunes track numbers gets renumbered
    /// sequentially — UNIQUE(disc_id, track_number) on save would otherwise fail.
    /// </summary>
    [Fact]
    public void Album_WithDuplicateTrackNumbers_RenumbersSequentially()
    {
        var tracks = new[]
        {
            Track(1, "First",  album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
            Track(2, "Second", album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
            Track(3, "Third",  album: "Best of",  trackNumber: 1, composer: "X, Y (1900-2000)"),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var only = Assert.Single(result.NewAlbums);
        var disc = Assert.Single(only.Discs);
        Assert.Equal(3, disc.Tracks.Count);
        Assert.Equal(new[] { 1, 2, 3 }, disc.Tracks.Select(t => t.TrackNumber));
    }

    /// <summary>
    /// Mixed scenario: a real album where some tracks have track numbers and
    /// some don't. Renumber-on-any-non-positive kicks the whole disc into
    /// sequential numbering so no track lands at 0.
    /// </summary>
    [Fact]
    public void Album_WithSomeMissingTrackNumbers_RenumbersWholeDisc()
    {
        var tracks = new[]
        {
            Track(1, "A", album: "Mix", trackNumber: 1,    composer: "X, Y (1900-2000)"),
            Track(2, "B", album: "Mix", trackNumber: null, composer: "X, Y (1900-2000)"),
            Track(3, "C", album: "Mix", trackNumber: 3,    composer: "X, Y (1900-2000)"),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        var only = Assert.Single(result.NewAlbums);
        var disc = Assert.Single(only.Discs);
        Assert.All(disc.Tracks, t => Assert.True(t.TrackNumber >= 1));
        Assert.Equal(disc.Tracks.Count, disc.Tracks.Select(t => t.TrackNumber).Distinct().Count());
    }

    // ── Contributor credits (composer + completer / arranger / etc.) ──────────

    /// <summary>
    /// Real-world case: Puccini's Turandot was completed by Franco Alfano. iTunes
    /// encodes this as <c>"Puccini, Giacomo (1858–1924), compl. Franco Alfano
    /// (1875–1954)"</c>. The importer must split the field into principal +
    /// contributor, normalise the contributor name to surname-first, register
    /// both as CanonComposers, and record the credit on the new piece.
    /// </summary>
    [Fact]
    public void CompoundComposer_PrincipalAndCompleter_CreatesBothAndCreditsPiece()
    {
        var tracks = new[]
        {
            Track(1, "Turandot - Act 1 - 1. In questa Reggia", album: "Turandot",
                  trackNumber: 1,
                  composer: "Puccini, Giacomo (1858–1924), compl. Franco Alfano (1875–1954)"),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var result = ItunesImporter.Import(tracks, composers, pieces);

        // Two composers were created: Puccini (principal) and Alfano (contributor).
        Assert.Equal(2, composers.Count);
        var puccini = composers.Single(c => c.Name == "Puccini, Giacomo");
        var alfano  = composers.Single(c => c.Name == "Alfano, Franco");
        Assert.Equal("1858", puccini.BirthDate);
        Assert.Equal("1924", puccini.DeathDate);
        Assert.Equal("1875", alfano.BirthDate);
        Assert.Equal("1954", alfano.DeathDate);
        Assert.Equal(2, result.NewComposers);

        // The piece carries the principal in Composer; Composers holds the
        // contributors only (no duplicated principal entry).
        var piece = Assert.Single(pieces);
        Assert.Equal("Puccini, Giacomo", piece.Composer);
        var contrib = Assert.Single(piece.Composers!);
        Assert.Equal("Alfano, Franco", contrib.Name);
        Assert.Equal("compl.", contrib.Role);
    }

    /// <summary>
    /// Contributor whose iTunes name is already surname-first
    /// (<c>"Busoni, Ferruccio"</c>) must be preserved verbatim — the heuristic
    /// surname-flip only fires when there's no comma in the contributor segment.
    /// </summary>
    [Fact]
    public void CompoundComposer_ContributorAlreadySurnameFirst_KeptVerbatim()
    {
        var tracks = new[]
        {
            Track(1, "Chaconne", album: "Bach Transcriptions",
                  trackNumber: 1,
                  composer: "Bach, Johann Sebastian (1685-1750), arr. Busoni, Ferruccio (1866-1924)"),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        ItunesImporter.Import(tracks, composers, pieces);

        Assert.Equal(2, composers.Count);
        Assert.Contains(composers, c => c.Name == "Bach, Johann Sebastian");
        Assert.Contains(composers, c => c.Name == "Busoni, Ferruccio");

        var piece = Assert.Single(pieces);
        var contrib = Assert.Single(piece.Composers!);
        Assert.Equal("arr.", contrib.Role);
        Assert.Equal("Busoni, Ferruccio", contrib.Name);
    }

    /// <summary>
    /// Real-world case from the user: <c>"Fugue in G, BWV 577 (arr.)"</c> by
    /// Bach, arranged by Holst. The piece's <see cref="CanonPiece.Composer"/>
    /// must hold Bach (the principal) and <see cref="CanonPiece.Composers"/>
    /// must hold only Holst (the contributor) — NOT Bach again, because the
    /// PieceEditorWindow shows Composers verbatim under "Other Contributors".
    /// Previously the importer added a no-role principal entry that the
    /// editor displayed as a duplicate "Bach" contributor next to Holst.
    /// </summary>
    [Fact]
    public void CompoundComposer_PrincipalNotDuplicatedInOtherContributors()
    {
        var tracks = new[]
        {
            Track(1, "Fugue in G, BWV 577 (arr.)", album: "Holst Transcriptions",
                  trackNumber: 1,
                  composer: "Bach, Johann Sebastian (1685-1750), arr. Gustav Holst (1874-1934)"),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        ItunesImporter.Import(tracks, composers, pieces);

        // Both composers registered.
        Assert.Equal(2, composers.Count);
        Assert.Contains(composers, c => c.Name == "Bach, Johann Sebastian");
        Assert.Contains(composers, c => c.Name == "Holst, Gustav");

        var piece = Assert.Single(pieces);
        Assert.Equal("Bach, Johann Sebastian", piece.Composer);

        // Composers list contains the contributor only — no Bach duplicate.
        var contributor = Assert.Single(piece.Composers!);
        Assert.Equal("Holst, Gustav", contributor.Name);
        Assert.Equal("arr.", contributor.Role);
        Assert.DoesNotContain(piece.Composers!, c =>
            string.Equals(c.Name, "Bach, Johann Sebastian", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// End-to-end: an iTunes track like
    /// <c>"Symphony #11 in B-flat, Op. 34 - 1. Lento - Allegro agitato"</c>
    /// should produce ONE subpiece (movement #1) with TWO tempo markers,
    /// not two sibling subpieces. The leaf subpiece's Title is the joined
    /// tempi (matching the canon convention) and its Markers list carries
    /// the two tempi as numbered Tempo entries.
    /// </summary>
    [Fact]
    public void MultiTempoMovement_ImportsAsSingleSubpieceWithTempoMarkers()
    {
        var tracks = new[]
        {
            Track(1,
                  "Symphony #11 in B-flat, Op. 34 - 1. Lento - Allegro agitato",
                  album: "Test",
                  trackNumber: 1,
                  composer: "Beethoven, Ludwig van (1770-1827)"),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var result = ItunesImporter.Import(tracks, composers, pieces);

        var piece = Assert.Single(pieces);
        Assert.Equal("Symphony #11 in B-flat, Op. 34", piece.Title);

        var sub = Assert.Single(piece.Subpieces!);
        Assert.Equal("Lento - Allegro agitato", sub.Title);
        Assert.Equal("1", sub.MusicNumber);

        // Two Tempo markers, numbered 1 and 2, in order.
        Assert.NotNull(sub.Markers);
        Assert.Equal(2, sub.Markers!.Count);
        Assert.Equal(MarkerKind.Tempo, sub.Markers[0].Kind);
        Assert.Equal("Lento",          sub.Markers[0].Value);
        Assert.Equal(1,                sub.Markers[0].Number);
        Assert.Equal(MarkerKind.Tempo, sub.Markers[1].Kind);
        Assert.Equal("Allegro agitato", sub.Markers[1].Value);
        Assert.Equal(2,                sub.Markers[1].Number);

        // The track gets ONE piece-ref pointing at the multi-tempo movement,
        // not two refs pointing at two separate movements.
        var album = Assert.Single(result.NewAlbums);
        var albumTrack = album.Discs[0].Tracks[0];
        var pieceRef = Assert.Single(albumTrack.PieceRefs!);
        Assert.Equal(new[] { "Lento - Allegro agitato" }, pieceRef.SubpiecePath);
    }

    /// <summary>
    /// Existing simple composer fields still parse — no contributors, no
    /// Composers list on the resulting piece. Regression guard for the
    /// pre-existing parse path.
    /// </summary>
    [Fact]
    public void SimpleComposer_NoContributors_LeavesPieceComposersListNull()
    {
        var tracks = new[]
        {
            Track(1, "Symphony", album: "Beethoven 5",
                  trackNumber: 1, composer: "Beethoven, Ludwig van (1770-1827)"),
        };
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        ItunesImporter.Import(tracks, composers, pieces);

        Assert.Single(composers);
        Assert.Equal("Beethoven, Ludwig van", composers[0].Name);

        var piece = Assert.Single(pieces);
        Assert.Equal("Beethoven, Ludwig van", piece.Composer);
        Assert.Null(piece.Composers);
    }

    /// <summary>
    /// When a piece already exists in the canon (matched by composer + title),
    /// the importer must NOT overwrite its <c>Composers</c> list — the user has
    /// already curated it. Contributor CanonComposers still get added to the
    /// composers list so they're visible going forward.
    /// </summary>
    [Fact]
    public void CompoundComposer_PieceAlreadyExists_PreservesCuratedCredits()
    {
        var existingPiece = new CanonPiece
        {
            Composer = "Puccini, Giacomo",
            Title    = "Turandot",
            // Imagine the user has already curated a slightly different role
            // for the contributor (e.g. "compl. (revised)" instead of "compl.").
            Composers = new List<ComposerCredit>
            {
                new() { Name = "Alfano, Franco", Role = "compl. (revised)" },
            },
        };
        var pieces    = new List<CanonPiece> { existingPiece };
        var composers = new List<CanonComposer>
        {
            new() { Name = "Puccini, Giacomo", SortName = "Puccini, Giacomo" },
        };

        var tracks = new[]
        {
            Track(1, "Turandot - Act 1 - 1. In questa Reggia", album: "Turandot",
                  trackNumber: 1,
                  composer: "Puccini, Giacomo (1858-1924), compl. Franco Alfano (1875-1954)"),
        };

        ItunesImporter.Import(tracks, composers, pieces);

        // Alfano was added to the composers list (he wasn't there before),
        // but Puccini's existing piece keeps its curated credit untouched —
        // the importer must not churn the user's "compl. (revised)" back to
        // the default "compl." that ParseComposer would produce.
        Assert.Contains(composers, c => c.Name == "Alfano, Franco");
        Assert.Single(pieces);
        var preserved = Assert.Single(existingPiece.Composers!);
        Assert.Equal("Alfano, Franco",     preserved.Name);
        Assert.Equal("compl. (revised)",   preserved.Role);
    }

    /// <summary>
    /// Regression: when ParseTrackName couldn't extract a top-level piece
    /// title (an iTunes Name that doesn't follow the "Work - Movement"
    /// convention, or one that opens with the separator and so has an empty
    /// leading segment), the importer used to fabricate a piece-ref with
    /// <c>PieceTitle = ""</c>. The Tracks view rendered that as a blank
    /// cell, and Description was never populated either, so the user saw
    /// two empty columns with no clue what the iTunes row was. The new
    /// behaviour mirrors the no-composer fallback: stash the raw iTunes
    /// Name in Description and skip the PieceRef.
    /// </summary>
    [Fact]
    public void TrackWithComposerButUnparseableName_FallsBackToDescription()
    {
        var tracks = new[]
        {
            // Leading " - " makes the first segment empty → PieceTitle = "".
            Track(1, " - 1. Allegro", album: "Some Album",
                  composer: "Beethoven, Ludwig van (1770-1827)",
                  trackNumber: 1, discNumber: 1),
        };

        var result = ItunesImporter.Import(tracks,
            new List<CanonComposer>(), new List<CanonPiece>());

        var album   = Assert.Single(result.NewAlbums);
        var disc    = Assert.Single(album.Discs);
        var imported = Assert.Single(disc.Tracks);

        // Pre-fix: imported.PieceRefs == [TrackPieceRef{PieceTitle=""}] and
        // imported.Description == null. Post-fix: PieceRefs null, Description
        // carries the raw iTunes Name for the user to triage.
        Assert.True(imported.PieceRefs is null || imported.PieceRefs.Count == 0,
            "Expected no piece refs when the parse yields an empty piece title.");
        Assert.Equal(" - 1. Allegro", imported.Description);
    }

    /// <summary>
    /// Regression: importing a track whose movement matches a CURATED
    /// structural subpiece (Title="", Number=4, Form="Rondo") must not mutate
    /// that subpiece. Pre-fix, EnsureSubpiecePath back-filled the empty Title
    /// with the parsed segment, corrupting "4. Rondo" into the full tempo run
    /// "Rondo - Allegro, ma non troppo - Più allegro quasi presto", and could
    /// plant a bogus "Rondo" tempo marker. The album ref must still resolve to
    /// the existing movement (no new subpiece created).
    /// </summary>
    [Fact]
    public void ImportMatchingCuratedStructuralMovement_DoesNotMutateIt()
    {
        var sonata = new CanonPiece
        {
            Composer    = "Beethoven, Ludwig van",
            Form        = "Piano Sonata",
            Number      = 15,
            KeyTonality = "D",
            Nickname    = "Pastoral",
            CatalogInfo = new List<CatalogInfo> { new() { Catalog = "Op.", CatalogNumber = "28" } },
            Subpieces   = new List<CanonPiece>
            {
                new() { Composer = "Beethoven, Ludwig van", Number = 1 },
                new() { Composer = "Beethoven, Ludwig van", Number = 2 },
                new() { Composer = "Beethoven, Ludwig van", Form = "Scherzo", Number = 3 },
                new() { Composer = "Beethoven, Ludwig van", Form = "Rondo",   Number = 4 },
            },
        };
        var rondo = sonata.Subpieces![3];

        var pieces    = new List<CanonPiece> { sonata };
        var composers = new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        };

        var track = Track(19,
            "Piano Sonata #15 in D, Op. 28 \"Pastoral\" - 4. Rondo. Allegro, ma non troppo - Più allegro quasi presto",
            album: "Beethoven Piano Sonatas Jandó 10",
            trackNumber: 19, discNumber: 1,
            composer: "Beethoven, Ludwig van (1770-1827)");

        // Force FormAndTempo (what the dialog auto-picks for this track).
        var dotChoices = new Dictionary<int, ItunesImportInference.DotSeparatorInterpretation>
        {
            [19] = ItunesImportInference.DotSeparatorInterpretation.FormAndTempo,
        };

        var result = ItunesImporter.Import(track is null ? Array.Empty<ItunesTrack>() : new[] { track },
            composers, pieces, existingAlbums: null, dotInterpretations: dotChoices);

        // The curated movement is untouched.
        Assert.Equal("", rondo.Title ?? "");
        Assert.Equal(4, rondo.Number);
        Assert.Equal("Rondo", rondo.Form);
        Assert.True(rondo.Markers is null || rondo.Markers.Count == 0,
            "Matched curated movement must not gain tempo markers.");

        // No new subpiece was created under the sonata.
        Assert.Equal(4, sonata.Subpieces!.Count);
        Assert.Equal(0, result.NewSubpieces);

        // The album track ref resolves back to the existing movement #4.
        var album      = Assert.Single(result.NewAlbums);
        var albumTrack = album.Discs[0].Tracks[0];
        var pieceRef   = Assert.Single(albumTrack.PieceRefs!);
        var resolver   = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(pieces);
        var resolved = resolver.TryResolve(pieceRef);
        Assert.NotNull(resolved);
        Assert.Same(rondo, resolved!.Value.Piece);
    }

    /// <summary>
    /// Regression: a track whose title matches a MEMBER of a "set" container
    /// (e.g. "Three Piano Sonatas, WoO 47") must reuse that member, not create
    /// a duplicate top-level piece. Set members carry a null Composer of their
    /// own (they inherit it from the set parent), so the importer's
    /// resolved-piece composer-equality re-check used to fail against null and
    /// fabricate a duplicate. The resolver already keys by the inherited
    /// composer, so the hit is valid.
    /// </summary>
    [Fact]
    public void ImportSetMemberWithInheritedComposer_ReusesMember_NoDuplicate()
    {
        // A "set" of three piano sonatas; members have empty Title + null
        // Composer, identified structurally by Form + catalog (WoO 47 #N) +
        // key — exactly how the curated canon stores them.
        CanonPiece Member(string key, string sub) => new()
        {
            // Composer deliberately left null — inherited from the set parent.
            Form        = "Piano Sonata",
            KeyTonality = key,
            CatalogInfo = new List<CatalogInfo>
            {
                new() { Catalog = "WoO", CatalogNumber = "47", CatalogSubnumber = sub },
            },
        };

        var set = new CanonPiece
        {
            Composer    = "Beethoven, Ludwig van",
            Form        = "Set",
            CatalogInfo = new List<CatalogInfo> { new() { Catalog = "WoO", CatalogNumber = "47" } },
            Subpieces   = new List<CanonPiece>
            {
                Member("E-flat", "1"),
                Member("f",      "2"),
                Member("D",      "3"),
            },
        };

        var pieces    = new List<CanonPiece> { set };
        var composers = new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        };

        var tracks = new[]
        {
            Track(1, "Piano Sonata in E-flat, WoO 47 #1 - 1. Allegro cantabile",
                  album: "Beethoven Piano Sonatas Jandó 10",
                  trackNumber: 1, discNumber: 1,
                  composer: "Beethoven, Ludwig van (1770-1827)"),
        };

        var result = ItunesImporter.Import(tracks, composers, pieces);

        // No new TOP-LEVEL piece was created — only the existing set remains.
        Assert.Single(pieces);
        Assert.Same(set, pieces[0]);
        Assert.DoesNotContain(pieces, p => p.Title == "Piano Sonata in E-flat, WoO 47 #1");

        // The album ref resolves to the set member (its movement), not a duplicate.
        var album      = Assert.Single(result.NewAlbums);
        var albumTrack = album.Discs[0].Tracks[0];
        var pieceRef   = Assert.Single(albumTrack.PieceRefs!);
        var resolver   = new PieceReferenceIndex(registerAsCurrent: false);
        resolver.BuildResolver(pieces);
        var resolved = resolver.TryResolve(pieceRef);
        Assert.NotNull(resolved);
        // The resolved leaf is under the E-flat member, which is under the set.
        var eFlatMember = set.Subpieces![0];
        Assert.True(ReferenceEquals(resolved!.Value.Piece, eFlatMember) ||
                    (eFlatMember.Subpieces?.Contains(resolved.Value.Piece) ?? false),
            "Album ref should resolve to the set member (or its movement), not a duplicate top piece.");
    }

    /// <summary>
    /// Re-importing a GENUINE multi-tempo movement ("1. Lento - Allegro agitato")
    /// that matches an existing marker-less structural movement enriches that
    /// movement with the parsed tempo markers (it was missing them). Tempo
    /// continuation is genuine (not a FormAndTempo collapse), so it's safe.
    /// </summary>
    [Fact]
    public void ImportGenuineMultiTempo_MatchedMovementWithNoMarkers_GetsEnriched()
    {
        var movement = new CanonPiece { Composer = "X", Number = 1 };  // structural, no title, no markers
        var top = new CanonPiece { Composer = "X", Title = "Symphony Test", Subpieces = new() { movement } };
        var pieces = new List<CanonPiece> { top };
        var composers = new List<CanonComposer> { new() { Name = "X", SortName = "X" } };

        var track = Track(1, "Symphony Test - 1. Lento - Allegro agitato",
            album: "A", trackNumber: 1, discNumber: 1, composer: "X");

        ItunesImporter.Import(new[] { track }, composers, pieces);

        Assert.Single(top.Subpieces!);              // no new subpiece created
        Assert.NotNull(movement.Markers);
        Assert.Equal(2, movement.Markers!.Count);
        Assert.Equal("Lento",           movement.Markers[0].Value);
        Assert.Equal("Allegro agitato", movement.Markers[1].Value);
        Assert.All(movement.Markers, m => Assert.Equal(MarkerKind.Tempo, m.Kind));
    }

    /// <summary>
    /// The FormAndTempo collapse must NOT enrich a matched curated movement —
    /// its "tempos" list leads with the FORM ("Rondo"), which as a tempo marker
    /// would corrupt curated data. Mirrors
    /// <see cref="ImportMatchingCuratedStructuralMovement_DoesNotMutateIt"/> but
    /// pins specifically that the no-markers movement stays marker-less.
    /// </summary>
    [Fact]
    public void ImportFormAndTempo_MatchedMovementWithNoMarkers_StaysMarkerless()
    {
        var rondo = new CanonPiece { Composer = "X", Form = "Rondo", Number = 4 };  // no markers
        var top = new CanonPiece { Composer = "X", Title = "Sonata Test", Subpieces = new() { rondo } };
        var pieces = new List<CanonPiece> { top };
        var composers = new List<CanonComposer> { new() { Name = "X", SortName = "X" } };

        var track = Track(1, "Sonata Test - 4. Rondo. Allegretto, ma non troppo",
            album: "A", trackNumber: 1, discNumber: 1, composer: "X");
        var dotChoices = new Dictionary<int, ItunesImportInference.DotSeparatorInterpretation>
        {
            [1] = ItunesImportInference.DotSeparatorInterpretation.FormAndTempo,
        };

        ItunesImporter.Import(new[] { track }, composers, pieces, existingAlbums: null,
            dotInterpretations: dotChoices);

        Assert.Single(top.Subpieces!);
        Assert.True(rondo.Markers is null || rondo.Markers.Count == 0,
            "FormAndTempo must not plant the form as a bogus tempo marker on a matched movement.");
    }
}
