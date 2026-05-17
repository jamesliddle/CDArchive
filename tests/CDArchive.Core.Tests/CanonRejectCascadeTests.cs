using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// End-to-end tests for the canon reject cascade. Each test builds a fresh
/// SQLite DB in a temp folder, seeds it via <see cref="SqliteCanonDataService"/>
/// (the same write path the WPF app uses), runs the cascade, then reads back
/// to confirm that composers, pieces, and album track refs were actually
/// deleted — i.e., that "they return on refresh" can no longer happen.
/// </summary>
public class CanonRejectCascadeTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteCanonDataService _svc;

    public CanonRejectCascadeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var dbPath  = Path.Combine(_tempDir, "ClassicalCanon.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        _svc = new SqliteCanonDataService(factory, json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
    {
        private readonly DbContextOptions<CanonDbContext> _options;
        public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
        public CanonDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Reproduces the user's reported bug: a provisional composer with a
    /// verbatim composite name (created by pre-fix iTunes import of Turandot)
    /// that owns one piece, with an album track referencing the piece. After
    /// reject the composer + piece + ref must all be gone, and a fresh load
    /// from SQLite must not bring any of them back.
    /// </summary>
    [Fact]
    public async Task RejectComposer_CompositeName_DeletesComposerPieceAndStripsRef()
    {
        // Mirror the OLD-parser behaviour: composer Name is the whole iTunes
        // string verbatim, the piece's Composer string matches, and an album
        // track has a TrackPieceRef pointing at the piece.
        const string compositeName = "Puccini, Giacomo (1858–1924), compl. Franco Alfano (1875–1954)";

        var composers = new List<CanonComposer>
        {
            new() { Name = compositeName, SortName = compositeName, IsProvisional = true },
        };
        var pieces = new List<CanonPiece>
        {
            new()
            {
                Composer      = compositeName,
                Title         = "Turandot",
                IsProvisional = true,
            },
        };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Turandot — Leinsdorf",
                Discs =
                {
                    new AlbumDisc
                    {
                        DiscNumber = 1,
                        Tracks =
                        {
                            new AlbumTrack
                            {
                                TrackNumber = 1,
                                PieceRefs   =
                                [
                                    new TrackPieceRef
                                    {
                                        Composer   = compositeName,
                                        PieceTitle = "Turandot",
                                    },
                                ],
                            },
                        },
                    },
                },
                IsProvisional = true,
            },
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);
        await _svc.SaveAlbumsAsync(albums);

        // Sanity: the seed actually landed.
        Assert.Single(await _svc.LoadComposersAsync());
        Assert.Single(await _svc.LoadPiecesAsync());
        var seededAlbums = await _svc.LoadAlbumsAsync();
        Assert.Single(seededAlbums[0].Discs[0].Tracks[0].PieceRefs!);

        // Run the cascade.
        var result = await CanonRejectCascade.RejectComposerAsync(
            _svc, composers, pieces, composers[0]);

        Assert.Equal(1, result.PiecesDeleted);
        Assert.Equal(1, result.RefsStripped);
        Assert.Equal(0, result.CreditsStripped);

        // Reload from SQLite — this is the "refresh" path that previously brought
        // the rejected composer back. Now it must stay gone.
        var afterComposers = await _svc.LoadComposersAsync();
        var afterPieces    = await _svc.LoadPiecesAsync();
        var afterAlbums    = await _svc.LoadAlbumsAsync();

        Assert.Empty(afterComposers);
        Assert.Empty(afterPieces);
        // The album survives but its track is now uncatalogued.
        var album = Assert.Single(afterAlbums);
        var track = album.Discs[0].Tracks[0];
        Assert.True(track.PieceRefs is null or { Count: 0 },
            "Expected the orphan TrackPieceRef row to be removed from the DB.");
    }

    /// <summary>
    /// Reject a composer who is a contributor (not principal) on a surviving
    /// piece. The contributor-credit row referencing that composer must be
    /// stripped so the composer-row delete clears the
    /// <c>piece_composer_credits.composer_id</c> FK restrict.
    /// </summary>
    [Fact]
    public async Task RejectComposer_ContributorOnly_StripsCreditAndDeletesComposer()
    {
        const string alfano = "Alfano, Franco";

        var composers = new List<CanonComposer>
        {
            new() { Name = "Puccini, Giacomo", SortName = "Puccini, Giacomo", IsProvisional = true },
            new() { Name = alfano,             SortName = alfano,             IsProvisional = true },
        };
        var pieces = new List<CanonPiece>
        {
            new()
            {
                Composer      = "Puccini, Giacomo",
                Title         = "Turandot",
                IsProvisional = true,
                Composers     =
                [
                    new ComposerCredit { Name = "Puccini, Giacomo" },
                    new ComposerCredit { Name = alfano, Role = "compl." },
                ],
            },
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);

        var result = await CanonRejectCascade.RejectComposerAsync(
            _svc, composers, pieces, composers.Single(c => c.Name == alfano));

        Assert.Equal(0, result.PiecesDeleted);
        Assert.Equal(0, result.RefsStripped);
        Assert.Equal(1, result.CreditsStripped);

        var afterComposers = await _svc.LoadComposersAsync();
        var afterPieces    = await _svc.LoadPiecesAsync();

        Assert.Single(afterComposers);
        Assert.Equal("Puccini, Giacomo", afterComposers[0].Name);

        var puccini = Assert.Single(afterPieces);
        Assert.Equal("Turandot", puccini.Title);
        // The credit list either contains only Puccini, or is null because we
        // collapsed a one-entry list (either is acceptable for survival).
        if (puccini.Composers is { Count: > 0 } credits)
            Assert.DoesNotContain(credits, c =>
                string.Equals(c.Name, alfano, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Demonstrates the bug the cascade exists to fix: if the UI removes only
    /// the composer from the in-memory list and calls SaveComposersAsync without
    /// first stripping refs and deleting pieces, the FK-restrict chain
    /// (composer ← pieces ← album_track_piece_refs) trips on the orphan-delete
    /// pass and throws — but historically the exception was swallowed by the
    /// async-void command invocation, and the composer reappeared on refresh.
    /// </summary>
    [Fact]
    public async Task RejectComposer_WithoutCascade_ThrowsAndLeavesComposerInDb()
    {
        const string compositeName = "Puccini, Giacomo (1858–1924), compl. Franco Alfano (1875–1954)";

        var composers = new List<CanonComposer>
        {
            new() { Name = compositeName, SortName = compositeName, IsProvisional = true },
        };
        var pieces = new List<CanonPiece>
        {
            new() { Composer = compositeName, Title = "Turandot", IsProvisional = true },
        };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Turandot — Leinsdorf",
                Discs =
                {
                    new AlbumDisc
                    {
                        DiscNumber = 1,
                        Tracks =
                        {
                            new AlbumTrack
                            {
                                TrackNumber = 1,
                                PieceRefs   =
                                [
                                    new TrackPieceRef
                                    {
                                        Composer   = compositeName,
                                        PieceTitle = "Turandot",
                                    },
                                ],
                            },
                        },
                    },
                },
            },
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);
        await _svc.SaveAlbumsAsync(albums);

        // The OLD reject path: remove the composer from the in-memory list and
        // call save. The orphan-delete pass tries to delete the composer row
        // while pieces.composer_id still references it.
        composers.RemoveAt(0);
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await _svc.SaveComposersAsync(composers));
        Assert.Contains("still owns pieces", ex.Message);

        // And on "refresh" — the composer is back, because the delete failed.
        var afterComposers = await _svc.LoadComposersAsync();
        Assert.Single(afterComposers);
        Assert.Equal(compositeName, afterComposers[0].Name);
    }

    /// <summary>
    /// When a loose track references the rejected piece, the cascade has to
    /// scrub the ref from the loose track too — otherwise the piece-row delete
    /// fails on the <c>album_track_piece_refs.piece_id</c> FK restrict.
    /// </summary>
    [Fact]
    public async Task RejectPiece_StripsRefFromLooseTrack_AndDeletesPiece()
    {
        var composers = new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van", IsProvisional = false },
        };
        var piece = new CanonPiece
        {
            Composer      = "Beethoven, Ludwig van",
            Title         = "Für Elise",
            IsProvisional = true,
        };
        var pieces = new List<CanonPiece> { piece };
        var looseTrack = new AlbumTrack
        {
            Description = "Loose Für Elise download",
            PieceRefs   = [new TrackPieceRef { Composer = "Beethoven, Ludwig van", PieceTitle = "Für Elise" }],
            IsProvisional = true,
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { looseTrack });

        var result = await CanonRejectCascade.RejectPieceAsync(_svc, pieces, piece);

        Assert.Equal(1, result.PiecesDeleted);
        Assert.Equal(1, result.RefsStripped);

        var afterPieces      = await _svc.LoadPiecesAsync();
        var afterLooseTracks = await _svc.LoadLooseTracksAsync();

        Assert.Empty(afterPieces);
        var survivor = Assert.Single(afterLooseTracks);
        Assert.True(survivor.PieceRefs is null or { Count: 0 },
            "Expected the loose track's ref to be stripped after the cascade.");
        Assert.Equal("Loose Für Elise download", survivor.Description);
    }

    /// <summary>
    /// Composer-level reject must also scrub loose-track refs to any of the
    /// composer's pieces — otherwise the composer delete fails on the
    /// <c>pieces.composer_id</c> FK once we try to drop the now-orphan piece.
    /// </summary>
    [Fact]
    public async Task RejectComposer_StripsLooseTrackRefs_AndDeletesComposerAndPieces()
    {
        var composer = new CanonComposer
        {
            Name = "Test, Composer", SortName = "Test, Composer", IsProvisional = true,
        };
        var composers = new List<CanonComposer> { composer };
        var piece = new CanonPiece
        {
            Composer      = "Test, Composer",
            Title         = "Test Piece",
            IsProvisional = true,
        };
        var pieces = new List<CanonPiece> { piece };
        var looseTrack = new AlbumTrack
        {
            Description = "Loose recording",
            PieceRefs   = [new TrackPieceRef { Composer = "Test, Composer", PieceTitle = "Test Piece" }],
            IsProvisional = true,
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { looseTrack });

        var result = await CanonRejectCascade.RejectComposerAsync(_svc, composers, pieces, composer);

        Assert.Equal(1, result.PiecesDeleted);
        Assert.Equal(1, result.RefsStripped);

        Assert.Empty(await _svc.LoadComposersAsync());
        Assert.Empty(await _svc.LoadPiecesAsync());
        var survivor = Assert.Single(await _svc.LoadLooseTracksAsync());
        Assert.True(survivor.PieceRefs is null or { Count: 0 });
    }

    /// <summary>
    /// Reject a piece whose only album track ref points at it. The piece row
    /// and the ref row both have to be gone after the cascade, and the album
    /// itself survives with an uncatalogued track.
    /// </summary>
    [Fact]
    public async Task RejectPiece_StripsRefAndDeletesPiece()
    {
        var composers = new List<CanonComposer>
        {
            new() { Name = "Puccini, Giacomo", SortName = "Puccini, Giacomo", IsProvisional = true },
        };
        var piece = new CanonPiece
        {
            Composer      = "Puccini, Giacomo",
            Title         = "Turandot",
            IsProvisional = true,
        };
        var pieces = new List<CanonPiece> { piece };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Turandot — Leinsdorf",
                Discs =
                {
                    new AlbumDisc
                    {
                        DiscNumber = 1,
                        Tracks =
                        {
                            new AlbumTrack
                            {
                                TrackNumber = 1,
                                PieceRefs   =
                                [
                                    new TrackPieceRef
                                    {
                                        Composer   = "Puccini, Giacomo",
                                        PieceTitle = "Turandot",
                                    },
                                ],
                            },
                        },
                    },
                },
                IsProvisional = true,
            },
        };

        await _svc.SaveComposersAsync(composers);
        await _svc.SavePiecesAsync(pieces);
        await _svc.SaveAlbumsAsync(albums);

        var result = await CanonRejectCascade.RejectPieceAsync(_svc, pieces, piece);

        Assert.Equal(1, result.PiecesDeleted);
        Assert.Equal(1, result.RefsStripped);

        var afterPieces = await _svc.LoadPiecesAsync();
        var afterAlbums = await _svc.LoadAlbumsAsync();

        Assert.Empty(afterPieces);
        var album = Assert.Single(afterAlbums);
        Assert.True(album.Discs[0].Tracks[0].PieceRefs is null or { Count: 0 });
    }
}
