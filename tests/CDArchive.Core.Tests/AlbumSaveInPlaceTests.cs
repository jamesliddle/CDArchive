using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Regression tests for the load-mutate-save album persistence path. Locks in
/// that re-saving an album updates rows in place rather than deleting and
/// reinserting — row IDs survive content edits, a constraint violation rolls
/// back without wiping the table, and re-saves don't churn unrelated children.
/// </summary>
public class AlbumSaveInPlaceTests
{
    private static SqliteCanonDataService NewService(out string dbPath, out IDbContextFactory<CanonDbContext> factory)
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_album_inplace_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    private static async Task SeedComposerAndPieceAsync(SqliteCanonDataService svc)
    {
        await svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
        });
        await svc.SavePiecesAsync(new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Symphony No. 9" },
        });
    }

    private static CanonAlbum BuildAlbum(string title, int trackCount, params int[] discNumbers)
    {
        var discs = discNumbers.Length == 0 ? new[] { 1 } : discNumbers;
        return new CanonAlbum
        {
            Title = title,
            Discs = discs.Select(dn => new AlbumDisc
            {
                DiscNumber = dn,
                Tracks = Enumerable.Range(1, trackCount).Select(tn => new AlbumTrack
                {
                    TrackNumber = tn,
                    Description = $"track {tn}",
                }).ToList(),
            }).ToList(),
        };
    }

    private static async Task<Dictionary<(int Disc, int Track), long>> SnapshotTrackIdsAsync(
        IDbContextFactory<CanonDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.AlbumTracks
            .Include(t => t.Disc)
            // .Disc is guaranteed populated by the Include above; null-forgiving
            // silences CS8602 (the compiler can't see EF's load guarantee).
            .ToDictionaryAsync(t => (t.Disc!.DiscNumber, t.TrackNumber), t => t.Id);
    }

    /// <summary>
    /// Album-aware snapshot used by multi-album tests where the (Disc, Track)
    /// tuple isn't unique across albums.
    /// </summary>
    private static async Task<Dictionary<(string Key, int Disc, int Track), long>>
        SnapshotTrackIdsByAlbumAsync(IDbContextFactory<CanonDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rows = await db.AlbumTracks
            // d!.Album silences the ThenInclude-lambda CS8602; the projection
            // below uses Disc!.Album! for the same reason.
            .Include(t => t.Disc).ThenInclude(d => d!.Album)
            .Select(t => new
            {
                AlbumKey = (t.Disc!.Album!.Label ?? "") + "|" + (t.Disc.Album.CatalogueNumber ?? "")
                                                       + "|" + (t.Disc.Album.Title ?? ""),
                t.Disc.DiscNumber,
                t.TrackNumber,
                t.Id,
            })
            .ToListAsync();
        return rows.ToDictionary(r => (r.AlbumKey, r.DiscNumber, r.TrackNumber), r => r.Id);
    }

    /// <summary>
    /// Re-saving an album with one track's Description changed must keep every
    /// track row's primary key — the original delete-and-rebuild path threw
    /// away every row and reassigned IDs on every save.
    /// </summary>
    [Fact]
    public async Task ResavingEditedAlbum_PreservesTrackRowIds()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var album = BuildAlbum("Symphony 9 Live", trackCount: 4);
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            var idsBefore = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(4, idsBefore.Count);

            // Reload (kills the CWT identity, forcing the IdentityKey path).
            var reloaded = (await svc.LoadAlbumsAsync()).Single();
            reloaded.Discs[0].Tracks[2].Description = "edited";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { reloaded });

            var idsAfter = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(idsBefore.Count, idsAfter.Count);
            foreach (var (key, idBefore) in idsBefore)
                Assert.Equal(idBefore, idsAfter[key]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Renaming an album's Title via the editor's JSON-clone-and-substitute
    /// pattern must end with exactly one album in the DB carrying the new
    /// title — never two — and the disc/track structure must survive.
    ///
    /// <para>The editor JSON-clones the album for Cancel-safe editing, so the
    /// renamed instance passed to <c>SaveAlbumsAsync</c> has no CWT entry and
    /// the save falls back to <see cref="CanonAlbum.IdentityKey"/>. For an
    /// album without Label/CatalogueNumber the key folds in Title|Subtitle —
    /// so a rename produces a different key, the existing row becomes an
    /// orphan in the same transaction, and the renamed album inserts fresh.
    /// Row IDs churn (documented in CLAUDE.md), but the user-visible contract
    /// "one album in, one album out" must hold.</para>
    ///
    /// <para>Regression test for Top-5 #5 (H20): the file previously covered
    /// content-edit / add-track / remove-track / constraint-rollback / batch
    /// isolation, but nothing pinned the rename outcome — a regression that
    /// broke orphan-delete would silently leave a duplicate.</para>
    /// </summary>
    [Fact]
    public async Task RenamingAlbumTitle_LeavesOneAlbum_NoDuplicates()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var album = BuildAlbum("Original Title", trackCount: 4, 1, 2);
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            // Simulate the AlbumEditorWindow JSON-clone-and-substitute flow:
            // load → JSON-clone → mutate the clone → save the clone (no CWT
            // entry). This is the path that exercises IdentityKey-based dedup.
            var loaded   = (await svc.LoadAlbumsAsync()).Single();
            var loadedId = (await SnapshotAlbumIdsAsync(factory)).Single().Value;
            var clone    = JsonSerializer.Deserialize<CanonAlbum>(JsonSerializer.Serialize(loaded))!;
            clone.Title  = "Renamed Title";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { clone });

            // Exactly one album survives, carrying the new title.
            var afterIds = await SnapshotAlbumIdsAsync(factory);
            var only     = Assert.Single(afterIds);
            Assert.Equal("Renamed Title", only.Key);

            // Disc + track counts preserved through the orphan-delete +
            // reinsert dance.
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(2, await db.AlbumDiscs.CountAsync(d => d.AlbumId == only.Value));
            Assert.Equal(8, await db.AlbumTracks.CountAsync(
                t => t.DiscId != null && db.AlbumDiscs
                    .Where(d => d.AlbumId == only.Value)
                    .Select(d => d.Id)
                    .Contains(t.DiscId.Value)));

            // Row ID churned — documented behaviour (CLAUDE.md, "Album save:
            // load-mutate-save"). Asserted here so a future change that makes
            // rename ID-stable shows up as a deliberate test update, not a
            // silent contract drift.
            Assert.NotEqual(loadedId, only.Value);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    private static async Task<Dictionary<string, long>> SnapshotAlbumIdsAsync(
        IDbContextFactory<CanonDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Albums.ToDictionaryAsync(a => a.Title ?? "", a => a.Id);
    }

    /// <summary>
    /// Companion to the rename test above: an album with Label + CatalogueNumber
    /// also goes through the orphan-delete + reinsert dance on a Title rename
    /// (Title is part of <see cref="CanonAlbum.IdentityKey"/>, so any rename
    /// invalidates the key). This case is non-trivial because there's a
    /// <c>UNIQUE</c> filtered index on (label, catalogue_number) — the
    /// implementation must order the orphan-DELETE before the INSERT in the
    /// same transaction, or the index would reject the new row.
    /// </summary>
    [Fact]
    public async Task RenamingAlbumTitle_WithLabelAndCatalogue_StillEndsAsOneAlbum()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var album = BuildAlbum("Original Title", trackCount: 3);
            album.Label = "DG"; album.CatalogueNumber = "447-401";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            var loaded  = (await svc.LoadAlbumsAsync()).Single();
            var clone   = JsonSerializer.Deserialize<CanonAlbum>(JsonSerializer.Serialize(loaded))!;
            clone.Title = "Renamed Title";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { clone });

            // The (label, catalogue_number) unique index would have rejected
            // the insert if EF batched it before the orphan delete. Reaching
            // this assertion proves the ordering is correct.
            var only = Assert.Single(await SnapshotAlbumIdsAsync(factory));
            Assert.Equal("Renamed Title", only.Key);

            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(3, await db.AlbumTracks.CountAsync(
                t => t.DiscId != null && db.AlbumDiscs
                    .Where(d => d.AlbumId == only.Value)
                    .Select(d => d.Id)
                    .Contains(t.DiscId.Value)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Adding a track to an existing disc must insert exactly one new row;
    /// every other track keeps its row ID.
    /// </summary>
    [Fact]
    public async Task AddingOneTrack_OnlyInsertsTheNewRow()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var album = BuildAlbum("Mahler 1", trackCount: 4);
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });
            var idsBefore = await SnapshotTrackIdsAsync(factory);

            var reloaded = (await svc.LoadAlbumsAsync()).Single();
            reloaded.Discs[0].Tracks.Add(new AlbumTrack { TrackNumber = 5, Description = "encore" });
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { reloaded });

            var idsAfter = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(5, idsAfter.Count);
            foreach (var (key, idBefore) in idsBefore)
                Assert.Equal(idBefore, idsAfter[key]);
            // The new track's ID is greater than every prior ID.
            Assert.True(idsAfter[(1, 5)] > idsBefore.Values.Max());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Removing one track must delete exactly that row; surviving tracks keep
    /// their row IDs.
    /// </summary>
    [Fact]
    public async Task RemovingOneTrack_OnlyDeletesThatRow()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var album = BuildAlbum("Brahms 4", trackCount: 4);
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });
            var idsBefore = await SnapshotTrackIdsAsync(factory);

            var reloaded = (await svc.LoadAlbumsAsync()).Single();
            reloaded.Discs[0].Tracks.RemoveAt(2); // drop track #3
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { reloaded });

            var idsAfter = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(3, idsAfter.Count);
            Assert.False(idsAfter.ContainsKey((1, 3)));
            Assert.Equal(idsBefore[(1, 1)], idsAfter[(1, 1)]);
            Assert.Equal(idsBefore[(1, 2)], idsAfter[(1, 2)]);
            Assert.Equal(idsBefore[(1, 4)], idsAfter[(1, 4)]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Critical safety property: a constraint violation during album save must
    /// roll back, leaving the existing rows intact. Pre-fix, the
    /// delete-and-reinsert sequence ran in two non-atomic SaveChanges calls and
    /// a failure in the second wiped the table. We simulate the failure by
    /// passing two albums whose Label/CatalogueNumber composite collides
    /// (UNIQUE constraint on the filtered Label+CatalogueNumber index).
    /// </summary>
    [Fact]
    public async Task ConstraintViolation_RollsBack_LeavesExistingRowsIntact()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var seed = BuildAlbum("Bach Cello Suites", trackCount: 6);
            seed.Label = "DG";
            seed.CatalogueNumber = "111-222";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { seed });

            var idsBefore = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(6, idsBefore.Count);

            // Bad batch: two albums with the same (Label, CatalogueNumber)
            // composite. The unique index on (label, catalogue_number) filtered
            // to both-non-null will reject the second one. Pre-fix this killed
            // the seed album's rows.
            var existingReloaded = (await svc.LoadAlbumsAsync()).Single();
            var conflicting = new CanonAlbum
            {
                Title           = "Different title",
                Label           = "DG",
                CatalogueNumber = "999-000",
            };
            var collider = new CanonAlbum
            {
                Title           = "Same key as existing",
                Label           = "DG",
                CatalogueNumber = "999-000",
                Discs           = [new AlbumDisc { DiscNumber = 1 }],
            };

            await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
                await svc.SaveAlbumsAsync(new List<CanonAlbum> { existingReloaded, conflicting, collider }));

            // Existing seed album survived intact; transaction rollback held.
            var idsAfter = await SnapshotTrackIdsAsync(factory);
            Assert.Equal(idsBefore.Count, idsAfter.Count);
            foreach (var (key, idBefore) in idsBefore)
                Assert.Equal(idBefore, idsAfter[key]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Editing one album in a multi-album batch must leave every other album's
    /// rows untouched — a regression of this property would mean we're still
    /// rebuilding albums we didn't actually need to.
    /// </summary>
    [Fact]
    public async Task EditingOneAlbumInBatch_LeavesOtherAlbumsRowsStable()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            var a = BuildAlbum("Album A", trackCount: 3); a.Label = "L"; a.CatalogueNumber = "A";
            var b = BuildAlbum("Album B", trackCount: 3); b.Label = "L"; b.CatalogueNumber = "B";
            var c = BuildAlbum("Album C", trackCount: 3); c.Label = "L"; c.CatalogueNumber = "C";
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { a, b, c });

            var idsBefore = await SnapshotTrackIdsByAlbumAsync(factory);
            Assert.Equal(9, idsBefore.Count);

            // Reload, edit only album B's track 2 description, save the full list.
            var reloaded = await svc.LoadAlbumsAsync();
            var bReloaded = reloaded.Single(x => x.CatalogueNumber == "B");
            bReloaded.Discs[0].Tracks[1].Description = "changed";
            await svc.SaveAlbumsAsync(reloaded);

            var idsAfter = await SnapshotTrackIdsByAlbumAsync(factory);
            Assert.Equal(idsBefore.Count, idsAfter.Count);
            foreach (var (key, idBefore) in idsBefore)
                Assert.Equal(idBefore, idsAfter[key]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Orphan-album delete must cascade to every child table (volumes, discs,
    /// tracks, piece refs, performers, sessions). C11's refactor stopped
    /// loading the orphan's full graph and now relies on SQLite's FK-Cascade
    /// configuration to clean up the children. This test seeds 3 albums with
    /// children populated across every cascade-FK table, then saves a list
    /// containing only one of them, and asserts that the other two are
    /// completely gone — no orphaned children left behind.
    /// </summary>
    [Fact]
    public async Task OrphanAlbumDelete_CascadesToEveryChildTable()
    {
        var dbPath = "";
        try
        {
            var svc = NewService(out dbPath, out var factory);
            await SeedComposerAndPieceAsync(svc);

            // Three albums, each with a Session (so the album_sessions cascade
            // is exercised), an album-level Performer, two discs (volumes hung
            // off the album), tracks with track-level Performers and PieceRefs.
            var albums = new[]
            {
                BuildOrphanFixtureAlbum("Album A", "L", "A"),
                BuildOrphanFixtureAlbum("Album B", "L", "B"),
                BuildOrphanFixtureAlbum("Album C", "L", "C"),
            };
            await svc.SaveAlbumsAsync(albums.ToList());

            // Snapshot row counts across every cascade-FK table.
            int countBefore;
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(3, await db.Albums.CountAsync());
                Assert.True(await db.AlbumVolumes.AnyAsync());
                Assert.True(await db.AlbumDiscs.CountAsync() >= 3);
                Assert.True(await db.AlbumTracks.CountAsync() >= 3);
                Assert.True(await db.AlbumPerformers.AnyAsync());
                Assert.True(await db.AlbumTrackPieceRefs.AnyAsync());
                countBefore = await db.Albums.CountAsync();
            }
            Assert.Equal(3, countBefore);

            // Reload (refreshes the CWT identity map), drop B and C from the
            // save list. Only A remains; B and C are orphans → cascade-delete.
            var reloaded = await svc.LoadAlbumsAsync();
            var keep = reloaded.Single(x => x.CatalogueNumber == "A");
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { keep });

            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.Albums.CountAsync());

                // Every child row of the orphaned albums must be gone — the
                // FK cascade is what's being tested. Any leftover row here
                // would mean the stub-attach Remove failed to reach the child.
                var aId = (await db.Albums.SingleAsync()).Id;
                Assert.True(await db.AlbumVolumes.AllAsync(v => v.AlbumId == aId));
                Assert.True(await db.AlbumDiscs.AllAsync(d => d.AlbumId == aId));
                Assert.True(await db.AlbumPerformers.AllAsync(p => p.AlbumId == null || p.AlbumId == aId));

                // Tracks live under disc; check via the disc join. AlbumTracks
                // for orphan albums should all be gone (no rows for discs that
                // no longer exist).
                var aDiscIds = await db.AlbumDiscs.Where(d => d.AlbumId == aId).Select(d => d.Id).ToListAsync();
                Assert.True(await db.AlbumTracks.AllAsync(t => t.DiscId == null || aDiscIds.Contains(t.DiscId!.Value)));

                // Track piece refs hang off track; same check via the track-id set.
                var aTrackIds = await db.AlbumTracks.Where(t => t.DiscId != null && aDiscIds.Contains(t.DiscId!.Value))
                    .Select(t => t.Id).ToListAsync();
                Assert.True(await db.AlbumTrackPieceRefs.AllAsync(r => aTrackIds.Contains(r.TrackId)));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Builds an album fixture that exercises every cascade-FK from the album
    /// row: volume, disc, track, album-level performer, and a piece ref
    /// hanging off the track. The composer/piece seeded by
    /// <see cref="SeedComposerAndPieceAsync"/> is the target of the piece ref.
    /// Session fields live as direct columns on the album now (post the
    /// sessions-as-fields refactor) — set them here so the round-trip
    /// covers them too.
    /// </summary>
    private static CanonAlbum BuildOrphanFixtureAlbum(string title, string label, string cat) => new()
    {
        Title           = title,
        Label           = label,
        CatalogueNumber = cat,
        SessionDates    = "1962-03-01",
        SessionVenue    = "Musikverein",
        SessionCity     = "Vienna",
        Volumes         = new List<AlbumVolume>
        {
            new() { Number = 1, Title = "Vol I" },
        },
        Performers      = new List<AlbumPerformer>
        {
            new() { Name = "Test, Performer" },
        },
        Discs           = new List<AlbumDisc>
        {
            new()
            {
                DiscNumber = 1,
                Tracks = new List<AlbumTrack>
                {
                    new()
                    {
                        TrackNumber = 1,
                        Description = "Movement I",
                        Performers  = new List<AlbumPerformer>
                        {
                            new() { Name = "Track Soloist" },
                        },
                        PieceRefs   = new List<TrackPieceRef>
                        {
                            new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Symphony No. 9" },
                        },
                    },
                },
            },
        },
    };
}
