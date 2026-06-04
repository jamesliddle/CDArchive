using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Regression for the iTunes-import crash where saving a piece (or subpiece)
/// that carried freshly-created <see cref="MusicalMarker"/> entries (Id == 0)
/// blew up inside <c>SqliteCanonDataService.ReconcileMarkers</c> with
/// <c>InvalidOperationException: The property 'PieceMarkerRow.Id' has a
/// temporary value while attempting to change the entity's state to
/// 'Deleted'.</c>
///
/// <para>Pre-fix: <c>ReconcileMarkers</c>'s pass-1 added the new markers to
/// the live navigation collection via <c>attachToPiece</c>, then pass 2's
/// <c>existing.ToList()</c> re-enumerated that collection and tried to
/// <c>db.Remove</c> the just-added rows. EF refuses to Delete an entity
/// whose Id is still EF's temporary placeholder.</para>
///
/// <para>The user-visible trigger was importing an iTunes-side album after
/// rejecting an existing provisional canon copy — <c>ItunesImporter</c>
/// populates each leaf subpiece's <c>Markers</c> with one Id=0 Tempo marker
/// per inferred tempo.</para>
/// </summary>
public class PieceMarkerReconcileRegressionTests
{
    private static SqliteCanonDataService NewService()
    {
        var dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_markerreconcile_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    [Fact]
    public async Task SavingFreshPieceWithMarkers_DoesNotThrowOnTemporaryId()
    {
        var svc = NewService();
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Sonata test piece",
            Markers  = new List<MusicalMarker>
            {
                // Both with Id == 0 — newly created in memory, never persisted.
                // Pre-fix this would crash inside ReconcileMarkers's pass 2.
                new() { Kind = MarkerKind.Tempo, Value = "Allegro" },
                new() { Kind = MarkerKind.Tempo, Value = "Andante" },
            },
        };

        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var loaded = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal("Sonata test piece", loaded.Title);
        Assert.NotNull(loaded.Markers);
        Assert.Equal(2, loaded.Markers!.Count);
        Assert.Equal("Allegro", loaded.Markers[0].Value);
        Assert.Equal("Andante", loaded.Markers[1].Value);
        // After save the rows have real DB ids.
        Assert.All(loaded.Markers, m => Assert.NotEqual(0L, m.Id));
    }

    [Fact]
    public async Task SavingExistingPieceWithFreshSubpieceMarkers_DoesNotThrow()
    {
        // Reproduces the iTunes-import path more directly: a parent piece
        // already exists in the canon; the importer adds a subpiece whose
        // Markers list has fresh Id=0 entries. UpsertPieceTree recurses into
        // the subpiece, ReplaceMarkersPiece runs on an empty existing-markers
        // collection, and pass 2's orphan walk used to see the just-added
        // rows.
        var svc = NewService();
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

        // First save — parent piece, no subpieces, no markers.
        var parent = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Piano Sonata",
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { parent });

        // Second save — same parent, now with a subpiece that has Id=0 markers.
        parent.Subpieces = new List<CanonPiece>
        {
            new()
            {
                Composer = "Beethoven, Ludwig van",
                Title    = "Allegro",
                Markers  = new List<MusicalMarker>
                {
                    new() { Kind = MarkerKind.Tempo, Value = "Allegro con brio" },
                },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { parent });

        var loaded = (await svc.LoadPiecesAsync()).Single();
        Assert.NotNull(loaded.Subpieces);
        var sub = loaded.Subpieces!.Single();
        Assert.NotNull(sub.Markers);
        var marker = sub.Markers!.Single();
        Assert.Equal("Allegro con brio", marker.Value);
        Assert.NotEqual(0L, marker.Id);
    }

    [Fact]
    public async Task MixedExistingAndFreshMarkers_KeepsExistingIdsAndAddsNewOnes()
    {
        // After a save, edit the piece: keep one existing marker (preserving
        // its DB id per the H31 contract), add a fresh marker with Id=0,
        // remove the other existing marker. The reconcile pass must:
        //  - leave the kept row's Id intact,
        //  - persist the new row with a fresh DB id,
        //  - delete the orphaned row.
        var svc = NewService();
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven" },
        });

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Sonata mixed-markers test",
            Markers  = new List<MusicalMarker>
            {
                new() { Kind = MarkerKind.Tempo, Value = "Allegro" },
                new() { Kind = MarkerKind.Tempo, Value = "Andante" },
            },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { piece });

        var afterFirst = (await svc.LoadPiecesAsync()).Single();
        var keptId = afterFirst.Markers![0].Id;
        Assert.NotEqual(0L, keptId);

        // Edit: drop "Andante", keep "Allegro" (with its real id), add "Presto" (Id=0).
        afterFirst.Markers = new List<MusicalMarker>
        {
            new() { Id = keptId, Kind = MarkerKind.Tempo, Value = "Allegro" },
            new() {               Kind = MarkerKind.Tempo, Value = "Presto"  },
        };
        await svc.SavePiecesAsync(new List<CanonPiece> { afterFirst });

        var afterSecond = (await svc.LoadPiecesAsync()).Single();
        Assert.Equal(2, afterSecond.Markers!.Count);
        Assert.Equal("Allegro", afterSecond.Markers[0].Value);
        Assert.Equal(keptId,    afterSecond.Markers[0].Id);
        Assert.Equal("Presto",  afterSecond.Markers[1].Value);
        Assert.NotEqual(0L,     afterSecond.Markers[1].Id);
        Assert.NotEqual(keptId, afterSecond.Markers[1].Id);
    }
}
