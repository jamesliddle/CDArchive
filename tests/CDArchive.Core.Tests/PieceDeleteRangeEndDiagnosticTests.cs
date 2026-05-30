using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// M3 regression: when a piece deletion fails because the piece is still
/// referenced as a range-end marker (the <c>EndPieceId</c> FK on
/// <c>album_track_piece_refs</c>, which has <c>OnDelete:Restrict</c>), the
/// surfaced <see cref="InvalidOperationException"/> message must name the
/// kind of reference that blocked the delete — pre-fix the message said
/// "remove those album references first" but the user looked at the album's
/// track refs and saw nothing pointing at the piece (the start-refs were
/// already stripped by the cascade; only the END-refs remained, and those
/// have a different visual shape — they're range markers).
/// </summary>
public class PieceDeleteRangeEndDiagnosticTests
{
    private static SqliteCanonDataService NewService(out IDbContextFactory<CanonDbContext> factory)
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_m3_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    [Fact]
    public async Task SavePieces_DeletingPieceUsedAsRangeEnd_SurfacesNameOfFailureMode()
    {
        var svc = NewService(out var factory);

        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });

        // Two top-level pieces. "PieceA" is the start of a range; "PieceB"
        // is the end. The track ref carries SubpiecePath null + EndSubpiecePath
        // pointing at PieceB via the same composer + a synthetic path that
        // Bottom-Up-walks to a real piece.
        //
        // Simpler shape that exercises the FK: both refs sit on the same
        // track. The album save's BuildTrackPieceRef resolves PieceB as
        // EndPiece by walking from EndSubpiecePath. To do that within the
        // resolver's vocabulary, PieceB needs to be reachable from PieceA's
        // top-level title via a subpiece path — or PieceB needs to be a
        // standalone piece whose title appears in EndSubpiecePath.
        //
        // The resolver can address a top-level piece directly when its
        // title equals the PieceTitle (no path needed). For the END piece,
        // ResolvePieceRef builds a synthetic probe with the same composer +
        // pieceTitle but the EndSubpiecePath as the path. So we need EndSubpiece
        // to be reachable as a subpiece of PieceA. Construct: PieceA contains
        // PieceA-end (subpiece). The range is (start=PieceA, end=PieceA-end).
        //
        // Now the test: try to delete the SUBPIECE PieceA-end. SQLite cascade
        // from PieceA's delete won't apply (we're not deleting PieceA). Instead
        // we delete just the subpiece — but EF's piece-tree delete walks the
        // parent tree, so deleting a subpiece independently isn't supported
        // through SavePiecesAsync. Hmm.
        //
        // Simpler: construct two separate top-level pieces. End-range refs
        // CAN address either (the resolver's only constraint is they share
        // composer + piece title… wait, no — the synthetic probe uses the
        // same PieceTitle. So EndPiece must be reachable under PieceTitle).
        //
        // I'm going to take a more direct route — manipulate the DB to plant
        // an album_track_piece_refs row with end_piece_id pointing at a piece
        // we then try to delete. Bypassing the resolver lets us isolate the
        // FK Restrict + diagnostic.

        var endPiece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Range-end target",
        };
        var keepPiece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Some other piece",
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { endPiece, keepPiece });

        var savedPieces = await svc.LoadPiecesAsync();
        var endPieceId = await GetPieceIdAsync(factory, "Range-end target");
        var keepPieceId = await GetPieceIdAsync(factory, "Some other piece");

        // Build an album with a track whose piece-ref points to "Some other
        // piece" as start AND has end_piece_id set to "Range-end target".
        // We plant the end_piece_id directly via SQL because the model-level
        // BuildTrackPieceRef would require both to be reachable through one
        // PieceTitle walk; this isolates the FK semantics.
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Albums.Add(new AlbumRow
            {
                Title = "Test album",
                IsProvisional = false,
                Discs = new List<AlbumDiscRow>
                {
                    new AlbumDiscRow
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrackRow>
                        {
                            new AlbumTrackRow
                            {
                                TrackNumber = 1,
                                IsProvisional = false,
                                PieceRefs = new List<AlbumTrackPieceRefRow>
                                {
                                    new AlbumTrackPieceRefRow
                                    {
                                        Position    = 0,
                                        PieceId     = keepPieceId,
                                        EndPieceId  = endPieceId,
                                    }
                                }
                            }
                        }
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        // Now try to delete "Range-end target" — its piece_id has 0 refs
        // (no start-refs target it), but its end_piece_id has 1 ref. The
        // FK Restrict fires and SavePiecesAsync must raise an
        // InvalidOperationException whose message mentions "range-end".
        var pieceList = savedPieces.Where(p => p.Title != "Range-end target").ToList();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.SavePiecesAsync(pieceList));

        Assert.Contains("range-end marker", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", ex.Message);  // "1 as a range-end marker"
    }

    [Fact]
    public async Task SavePieces_DeletingPieceWithStartRefs_StillSurfacesStartCount()
    {
        // Reverse coverage: the start-ref case should mention "as the piece
        // itself" (not range-end). Pre-M3 this case worked via the generic
        // message; M3's improvement adds the explicit "kind" suffix.
        var svc = NewService(out var factory);

        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven", SortName = "Beethoven" },
        });

        var p = new CanonPiece { Composer = "Beethoven", Title = "Test Piece" };
        await svc.SavePiecesAsync(new List<CanonPiece> { p });

        var saved = (await svc.LoadPiecesAsync()).Single();
        var album = new CanonAlbum
        {
            Title = "Album",
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new()
                        {
                            TrackNumber = 1,
                            PieceRefs = new List<TrackPieceRef>
                            {
                                new() { Composer = saved.Composer!, PieceTitle = saved.Title! },
                            }
                        }
                    }
                }
            }
        };
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Try to delete the piece — start-ref still in place.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await svc.SavePiecesAsync(new List<CanonPiece>()));

        Assert.Contains("as the piece itself", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("range-end", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> GetPieceIdAsync(IDbContextFactory<CanonDbContext> factory, string title)
    {
        await using var db = await factory.CreateDbContextAsync();
        return db.Pieces.AsNoTracking().Single(p => p.Title == title).Id;
    }
}
