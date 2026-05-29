using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// M5: <see cref="ItunesImporter"/> should not create duplicate canon albums
/// on re-import. When the importer sees an iTunes album whose <c>(Title,
/// normalised first performer)</c> matches an existing canon album, it
/// merges new tracks into the existing album instead of building a fresh
/// <see cref="CanonAlbum"/>.
///
/// <para>H24 was the symmetric fix on the preview-pane "already imported"
/// filter — keying that filter on the same <c>(album, performer, disc,
/// track)</c> tuple stopped two same-titled albums (e.g. Karajan vs
/// Bernstein's Beethoven 9) from colliding. M5 closes the loop in the
/// import pipeline so the same key shape governs both halves.</para>
/// </summary>
public class ItunesImporterAlbumDedupTests
{
    private static ItunesTrack Track(
        int trackId,
        string name,
        string album,
        string composer,
        string? artist = null,
        int? trackNumber = null,
        int? discNumber = null)
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
            AlbumArtist:  artist,
            Artist:       artist,
            DateAdded:    DateTime.UtcNow,
            Location:     null);

    // ── No existing album: behaves as pre-M5 ──────────────────────────────

    [Fact]
    public void NoExistingAlbums_BuildsFreshAlbum_AsBefore()
    {
        var tracks = new[]
        {
            Track(1, "Symphony I",  "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 1),
        };

        var result = ItunesImporter.Import(
            tracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum>());

        Assert.Single(result.NewAlbums);
        Assert.Equal(0, result.ModifiedAlbums);
        Assert.Equal("Symphony 9", result.NewAlbums[0].Title);
    }

    [Fact]
    public void ExistingAlbumsNotPassed_BuildsFreshAlbum_BackCompat()
    {
        // Old API: caller didn't pass existingAlbums at all (default null).
        // Behaviour is identical to pre-M5.
        var tracks = new[]
        {
            Track(1, "Symphony I", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 1),
        };

        var result = ItunesImporter.Import(tracks, new List<CanonComposer>(), new List<CanonPiece>());

        Assert.Single(result.NewAlbums);
        Assert.Equal(0, result.ModifiedAlbums);
    }

    // ── Dedup match: merge into existing ──────────────────────────────────

    [Fact]
    public void MatchingExistingAlbum_AppendsTracks_DoesNotCreateNewAlbum()
    {
        var existing = new CanonAlbum
        {
            Title         = "Symphony 9",
            IsProvisional = false,   // already curated
            Performers    = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } },
            Discs         = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks     = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, Description = "movement 1 (existing)" },
                        new() { TrackNumber = 2, Description = "movement 2 (existing)" },
                    },
                },
            },
        };

        var newTracks = new[]
        {
            Track(101, "Symphony III", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 3),
        };

        var albums = new List<CanonAlbum> { existing };
        var result = ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: albums);

        Assert.Empty(result.NewAlbums);                      // no fresh CanonAlbum
        Assert.Equal(1, result.ModifiedAlbums);
        Assert.Single(albums);                                // not duplicated
        Assert.Equal(3, existing.Discs[0].Tracks.Count);     // appended
        Assert.Equal(3, existing.Discs[0].Tracks[2].TrackNumber);
        // Existing curation preserved.
        Assert.False(existing.IsProvisional);
        Assert.Equal("Karajan, Herbert von", existing.Performers![0].Name);
    }

    [Fact]
    public void RoundTrip_ImportThenReimport_DedupsViaCanonSidePerformerList_BugReportRegression()
    {
        // User-reported bug: import iTunes "Symphony 9" (Artist="Karajan, Herbert von"),
        // re-import the same data — pre-fix M5 didn't detect the match because the
        // canon side keyed on Performers[0].Name only. The iTunes import split the
        // Artist comma into TWO AlbumPerformer entries ("Karajan", "Herbert von"),
        // so the canon-side key was "karajan" while the iTunes-side key was
        // "herbertkarajanvon". They never matched.
        //
        // This test runs the import twice in sequence, mirroring the user's flow:
        // step 1 builds the canon album as the importer would; step 2 re-imports and
        // asserts M5 matches.
        var albums    = new List<CanonAlbum>();
        var composers = new List<CanonComposer>();
        var pieces    = new List<CanonPiece>();

        var itunesBatch = new[]
        {
            Track(1, "Symphony I", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 1),
            Track(2, "Symphony II", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 2),
        };

        // Step 1: first import — no existing albums, fresh CanonAlbum built.
        var r1 = ItunesImporter.Import(itunesBatch, composers, pieces, albums);
        Assert.Single(r1.NewAlbums);
        Assert.Equal(0, r1.ModifiedAlbums);
        albums.AddRange(r1.NewAlbums);
        // Confirm the importer comma-split Artist into TWO AlbumPerformer entries —
        // the exact configuration that broke the pre-fix dedup.
        Assert.Equal(2, albums[0].Performers!.Count);
        Assert.Equal("Karajan",      albums[0].Performers![0].Name);
        Assert.Equal("Herbert von",  albums[0].Performers![1].Name);

        // Step 2: re-import. M5 must dedup against the canon album from step 1.
        var r2 = ItunesImporter.Import(itunesBatch, composers, pieces, albums);
        Assert.Empty(r2.NewAlbums);              // no fresh CanonAlbum
        Assert.Equal(1, r2.ModifiedAlbums);     // existing matched
        Assert.Single(albums);                   // still one album in the list
    }

    [Fact]
    public void MatchingExistingAlbum_PerformerNameFormattingDiffers_StillMatches()
    {
        // Existing album has performer "Karajan, Herbert von"; iTunes Artist
        // is "Herbert von Karajan". H24's NormalisePerformer collapses both
        // to the same key — dedup should match.
        var existing = new CanonAlbum
        {
            Title      = "Symphony 9",
            Performers = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } },
            Discs      = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, Tracks = new List<AlbumTrack>() },
            },
        };

        var newTracks = new[]
        {
            Track(1, "Symphony I", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Herbert von Karajan", trackNumber: 1),
        };

        var result = ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum> { existing });

        Assert.Empty(result.NewAlbums);
        Assert.Equal(1, result.ModifiedAlbums);
    }

    [Fact]
    public void SameTitleDifferentPerformer_TreatedAsDifferentAlbum()
    {
        // The H24 motivating case: Karajan's Beethoven 9 should NOT collide
        // with Bernstein's Beethoven 9 — they're different albums even though
        // they share a Title.
        var existing = new CanonAlbum
        {
            Title      = "Symphony 9",
            Performers = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } },
            Discs      = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, Tracks = new List<AlbumTrack>() },
            },
        };

        var newTracks = new[]
        {
            Track(1, "Symphony I", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Bernstein, Leonard", trackNumber: 1),
        };

        var albums = new List<CanonAlbum> { existing };
        var result = ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: albums);

        Assert.Single(result.NewAlbums);   // fresh album for Bernstein
        Assert.Equal(0, result.ModifiedAlbums);
    }

    [Fact]
    public void MergeIntoExistingDisc_PreservesExistingTracks_AppendsAfter()
    {
        var existing = new CanonAlbum
        {
            Title      = "Symphony 9",
            Performers = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } },
            Discs      = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1 }, new() { TrackNumber = 2 },
                    },
                },
            },
        };

        var newTracks = new[]
        {
            Track(1, "Symphony III", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 3, discNumber: 1),
            Track(2, "Symphony IV",  "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 4, discNumber: 1),
        };

        ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum> { existing });

        Assert.Single(existing.Discs);   // not added a 2nd disc
        var disc = existing.Discs[0];
        Assert.Equal(4, disc.Tracks.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, disc.Tracks.Select(t => t.TrackNumber));
    }

    [Fact]
    public void MergeIntoNewDisc_OnExistingAlbum_AddsDisc()
    {
        // Existing album has only disc 1; iTunes brings disc 2.
        var existing = new CanonAlbum
        {
            Title      = "Sonatas",
            Performers = new List<AlbumPerformer> { new() { Name = "Brendel" } },
            Discs      = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, Tracks = new List<AlbumTrack>
                {
                    new() { TrackNumber = 1 },
                }},
            },
        };

        var newTracks = new[]
        {
            Track(1, "Sonata IV", "Sonatas", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Brendel", trackNumber: 1, discNumber: 2),
        };

        ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum> { existing });

        Assert.Equal(2, existing.Discs.Count);
        Assert.Equal(2, existing.Discs[1].DiscNumber);
        Assert.Single(existing.Discs[1].Tracks);
    }

    [Fact]
    public void MergeIntoExistingDisc_TrackNumberCollision_RenumbersAppended()
    {
        // Existing disc has tracks 1,2,3. iTunes batch (re-runs of the same
        // import) has track 1 — would collide. Importer renumbers to next
        // free position so the existing curated tracks stay put.
        var existing = new CanonAlbum
        {
            Title      = "Symphony 9",
            Performers = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } },
            Discs      = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, Description = "kept" },
                        new() { TrackNumber = 2, Description = "kept" },
                        new() { TrackNumber = 3, Description = "kept" },
                    },
                },
            },
        };

        var newTracks = new[]
        {
            // Same number 1 as an existing track — collides.
            Track(99, "Symphony X", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 1, discNumber: 1),
        };

        ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum> { existing });

        var disc = existing.Discs[0];
        Assert.Equal(4, disc.Tracks.Count);
        // First three preserved with their original Descriptions.
        Assert.Equal("kept", disc.Tracks[0].Description);
        Assert.Equal("kept", disc.Tracks[1].Description);
        Assert.Equal("kept", disc.Tracks[2].Description);
        // New track went to position 4 (next free).
        Assert.Equal(4, disc.Tracks[3].TrackNumber);
    }

    [Fact]
    public void MergeIntoExistingAlbum_DoesNotMutateExistingAlbumScalars()
    {
        var existing = new CanonAlbum
        {
            Title           = "Symphony 9",
            Subtitle        = "kept",
            Label           = "DG",
            CatalogueNumber = "476 1276",
            IsProvisional   = false,
            Performers      = new List<AlbumPerformer>
            {
                new() { Name = "Karajan, Herbert von" },
                new() { Name = "Berlin Philharmonic"   },
            },
            Discs           = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, Tracks = new List<AlbumTrack>() },
            },
        };

        var newTracks = new[]
        {
            Track(1, "Symphony I", "Symphony 9", "Beethoven, Ludwig van (1770-1827)",
                  artist: "Karajan, Herbert von", trackNumber: 1),
        };

        ItunesImporter.Import(
            newTracks, new List<CanonComposer>(), new List<CanonPiece>(),
            existingAlbums: new List<CanonAlbum> { existing });

        Assert.Equal("kept",            existing.Subtitle);
        Assert.Equal("DG",              existing.Label);
        Assert.Equal("476 1276",        existing.CatalogueNumber);
        Assert.False(existing.IsProvisional);
        Assert.Equal(2, existing.Performers!.Count);
    }
}
