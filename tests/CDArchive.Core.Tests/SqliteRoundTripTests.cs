using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// End-to-end checks that go through <see cref="SqliteCanonDataService"/> against
/// the real <c>data/ClassicalCanon.db</c>, verifying that the round trip
/// SQLite → domain models → <see cref="PieceReferenceIndex"/> still produces
/// album hits for hand-picked pieces. Sets are particularly subtle (their hit
/// list is built post-hoc by <c>AggregateSetHits</c>) so they get explicit
/// coverage.
/// </summary>
public class SqliteRoundTripTests
{
    private static string FindDataDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(candidate) &&
                File.Exists(Path.Combine(candidate, "ClassicalCanon.db")))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find data/ClassicalCanon.db");
    }

    private static SqliteCanonDataService CreateService(string dataDir)
    {
        var dbPath = Path.Combine(dataDir, "ClassicalCanon.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(dataDir);
        return new SqliteCanonDataService(factory, json);
    }

    private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
    {
        private readonly DbContextOptions<CanonDbContext> _options;
        public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
        public CanonDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Regression: collaborative works (a parent piece whose subpieces each have
    /// a different composer) used to land in the DB with every movement assigned
    /// to the parent's composer. <em>L'éventail de Jeanne</em> is the canonical
    /// case — a 1927 ballet with 10 movements by 10 different French composers.
    /// The seeder and the runtime save path now honour subpiece-level
    /// <c>composer</c> overrides, falling back to the parent's composer when
    /// the subpiece doesn't specify one.
    /// </summary>
    [Fact]
    public async Task LeventailDeJeanne_MovementsHaveIndividualComposers()
    {
        var dataDir = FindDataDirectory();
        var svc     = CreateService(dataDir);

        var pieces = await svc.LoadPiecesAsync();
        var leventail = pieces.FirstOrDefault(p =>
            (p.Title ?? "").StartsWith("L'éventail", StringComparison.Ordinal));

        // Skip if the seeder hasn't been run with the (Various)-aware data.
        // We only assert the contract when the work is present.
        if (leventail is null) return;

        Assert.Equal("(Various)", leventail.Composer);
        Assert.NotNull(leventail.Subpieces);
        Assert.Equal(10, leventail.Subpieces!.Count);

        // Every movement must declare its own composer, none should inherit
        // the (Various) sentinel from the parent.
        foreach (var mov in leventail.Subpieces)
        {
            Assert.False(string.IsNullOrEmpty(mov.Composer),
                $"Movement #{mov.Number} ({mov.Title}) has no composer assigned " +
                $"— the seeder should resolve subpiece-level composer overrides.");
            Assert.NotEqual("(Various)", mov.Composer);
        }

        // Spot-check the famous ones — Ravel wrote the Fanfare (M.80).
        var fanfare = leventail.Subpieces.FirstOrDefault(p => p.Title == "Fanfare");
        Assert.NotNull(fanfare);
        Assert.Equal("Ravel, Maurice", fanfare!.Composer);
    }

    /// <summary>
    /// End-to-end: <see cref="CrossComposerSubpieceFinder"/> against the real
    /// SQLite-loaded piece tree should surface Ravel's Fanfare from L'éventail
    /// de Jeanne with the expected composite display title. Verifies that the
    /// load-path normalization preserves the per-movement composers needed for
    /// detection.
    /// </summary>
    [Fact]
    public async Task CrossComposerSubpieceFinder_SurfacesLeventailMovementsUnderTheirComposers()
    {
        var dataDir = FindDataDirectory();
        var svc     = CreateService(dataDir);

        var pieces = await svc.LoadPiecesAsync();

        // Skip cleanly when L'éventail isn't seeded (e.g., a clean checkout
        // before the (Various) sentinel was added).
        var leventail = pieces.FirstOrDefault(p =>
            (p.Title ?? "").StartsWith("L'éventail", StringComparison.Ordinal));
        if (leventail is null) return;

        var byComposer = CrossComposerSubpieceFinder.Find(pieces);

        // Each of the 10 contributors should have at least one cross-credit
        // entry pointing at their movement of L'éventail. This count drives
        // the +crossCredit term in CanonView.UpdatePieceCounts, ensuring
        // composers like Roussel — who have no other canon entries — still
        // show a piece count of ≥1 from their L'éventail contribution alone.
        var contributors = new[]
        {
            "Ravel, Maurice", "Ferroud, Pierre-Octave", "Ibert, Jacques",
            "Roland-Manuel, Alexis", "Delannoy, Marcel", "Roussel, Albert",
            "Milhaud, Darius", "Poulenc, Francis", "Auric, Georges",
            "Schmitt, Florent",
        };
        foreach (var name in contributors)
        {
            Assert.True(byComposer.ContainsKey(name),
                $"{name} should have a cross-credit entry for their movement of L'éventail.");
            var leventailEntries = byComposer[name].Where(n => n.TopPiece == leventail).ToList();
            Assert.NotEmpty(leventailEntries);
        }

        // Ravel's Fanfare is movement #1; its composite title is the format
        // the user requested.
        var ravelEntry = byComposer["Ravel, Maurice"]
            .FirstOrDefault(n => n.TopPiece == leventail);
        Assert.NotNull(ravelEntry);
        Assert.Equal("L'éventail de Jeanne - 1. Fanfare", ravelEntry!.DisplayTitle);
        Assert.Equal("Fanfare", ravelEntry.Subpiece.Title);

        // Beethoven Op. 2 (a normal, single-composer set) should NOT generate
        // any cross-credit entries — its movements all inherit Beethoven.
        if (byComposer.TryGetValue("Beethoven, Ludwig van", out var beethovenCrosses))
            Assert.DoesNotContain(beethovenCrosses, n =>
                n.TopPiece.CatalogInfo?.Any(c => c.Catalog == "Op." && c.CatalogNumber == "2") ?? false);
    }

    /// <summary>
    /// Architectural invariant: <see cref="SqliteCanonDataService"/> writes only
    /// to SQLite. The canonical JSON files must not be modified as a side-effect
    /// of any <c>Save*Async</c> call, since that's exactly the dual-write
    /// pattern that allowed JSON ↔ SQLite divergence to corrupt movement-level
    /// album refs in earlier revisions.
    ///
    /// <para>
    /// Previously this test ran every <c>Save*Async</c> against the production
    /// <c>data/ClassicalCanon.db</c>; a crash mid-test could leave the user's
    /// DB partially written, and parallel xUnit runs hit SQLite file-lock
    /// contention. The invariant under test is about Save *behaviour*, not
    /// about a specific data shape, so we now run it against a fresh
    /// temp-dir fixture: empty schema-initialised DB plus placeholder JSON
    /// files we control. The mtime check still proves the architectural
    /// guarantee — saves don't touch JSON — without risking production data.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SaveOperations_DoNotTouchJsonFiles()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"cdarchive-savecheck-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        try
        {
            // Drop placeholder JSON files alongside the DB — the test only
            // cares about their mtimes staying stable across the SQLite saves,
            // not their content. Any non-empty string works.
            var jsonPaths = new[]
            {
                Path.Combine(dataDir, "Classical Canon composers.json"),
                Path.Combine(dataDir, "Classical Canon pieces.json"),
                Path.Combine(dataDir, "Classical Canon albums.json"),
                Path.Combine(dataDir, "Classical Canon pick lists.json"),
            };
            foreach (var p in jsonPaths) File.WriteAllText(p, "[]");

            var svc = CreateService(dataDir);

            // Touch each subsystem once so SQLite's schema gets created and
            // the load-mutate-save merge has something to chew on. Using
            // SaveBatchAsync to land everything in one atomic write keeps
            // the fixture small.
            await svc.SaveBatchAsync(
                composers:   new List<CanonComposer>  { new() { Name = "Test, Composer", SortName = "Test, Composer" } },
                pieces:      new List<CanonPiece>     { new() { Composer = "Test, Composer", Title = "Test Piece" } },
                albums:      new List<CanonAlbum>(),
                looseTracks: new List<AlbumTrack>(),
                pickLists:   new CanonPickLists());

            // Re-stamp the JSONs to a known mtime so the comparison below is
            // not vulnerable to filesystem mtime granularity (e.g. FAT32's 2s).
            var stamp = DateTime.UtcNow.AddMinutes(-5);
            foreach (var p in jsonPaths) File.SetLastWriteTimeUtc(p, stamp);
            var before = jsonPaths.ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));

            // Round-trip every subsystem: load + save. With write-through
            // removed, none of these should leave a fingerprint outside SQLite.
            var composers = await svc.LoadComposersAsync();
            await svc.SaveComposersAsync(composers);
            var pieces = await svc.LoadPiecesAsync();
            await svc.SavePiecesAsync(pieces);
            var pickLists = await svc.LoadPickListsAsync();
            await svc.SavePickListsAsync(pickLists);
            var albums = await svc.LoadAlbumsAsync();
            await svc.SaveAlbumsAsync(albums);

            foreach (var p in jsonPaths)
            {
                var afterMtime = File.GetLastWriteTimeUtc(p);
                Assert.Equal(before[p], afterMtime);
            }
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections — clear the pool before
            // trying to delete the temp directory or Windows refuses the
            // recursive delete with "file in use".
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, recursive: true); }
            catch (IOException) { /* best-effort cleanup; temp dir is harmless if it lingers */ }
        }
    }

    /// <summary>
    /// Regression: <c>BuildTrackPieceRef</c> used to walk all the way up to the
    /// set container, producing refs of shape <c>(set-DisplayTitleShort, [member-title])</c>.
    /// That breaks for any composer whose multiple sets share a wrapper title —
    /// for Beethoven, "Three Piano Sonatas" matches Op. 2, Op. 10, Op. 31 and
    /// WoO 47, and <see cref="PieceReferenceIndex.RegisterPiece"/>'s <c>TryAdd</c>
    /// keeps only the first registrant. The fix stops the walk at set boundaries
    /// so the ref addresses members directly under their (catalog-bearing)
    /// titles, which the resolver registers without collision.
    /// </summary>
    [Fact]
    public async Task BeethovenOp2_HasAlbumHits_AfterSqliteRoundTrip()
    {
        var dataDir = FindDataDirectory();
        var svc     = CreateService(dataDir);

        var pieces = await svc.LoadPiecesAsync();
        var albums = await svc.LoadAlbumsAsync();
        var index  = new PieceReferenceIndex();
        index.Rebuild(pieces, albums);

        var op2 = pieces.FirstOrDefault(p =>
            p.Composer == "Beethoven, Ludwig van" &&
            string.Equals(p.Form, "set", StringComparison.OrdinalIgnoreCase) &&
            (p.CatalogInfo?.Any(c => c.Catalog == "Op." && c.CatalogNumber == "2") ?? false));
        Assert.NotNull(op2);
        Assert.NotNull(op2!.Subpieces);
        Assert.Equal(3, op2.Subpieces!.Count);

        // Each member should have hits — confirms the SQLite-reconstructed refs
        // resolve back to the right sonata, not to whichever same-named set won
        // the TryAdd race.
        foreach (var sonata in op2.Subpieces)
        {
            var n = index.CountForPiece(sonata);
            Assert.True(n > 0,
                $"Beethoven Op. 2 sonata #{sonata.Number} has no album hits — " +
                $"BuildTrackPieceRef is producing refs that collide with another " +
                $"set's wrapper title.");
        }

        // The set itself should also have hits via AggregateSetHits' intersection
        // pass — every album in the canon that contains all three Op. 2 sonatas
        // qualifies.
        Assert.True(index.CountForPiece(op2) > 0,
            "Beethoven Op. 2 set has 0 album hits — AggregateSetHits computed an " +
            "empty intersection even though the members have hits.");

        // Movement level — regression: PieceReferenceIndex.TryResolve used to
        // return entry.Piece (the title-lookup top) instead of the leaf, so the
        // seeder stored sonata-level piece_ids and movement info was silently
        // dropped at seed time. The fix returns the deepest matched subpiece;
        // each movement of each Op. 2 sonata should now show non-zero hits.
        foreach (var sonata in op2.Subpieces)
        {
            Assert.NotNull(sonata.Subpieces);
            Assert.True(sonata.Subpieces!.Count > 0,
                $"Sonata #{sonata.Number} has no movements in the canon.");
            foreach (var movement in sonata.Subpieces)
            {
                var n = index.CountForPiece(movement);
                Assert.True(n > 0,
                    $"Movement #{movement.Number} of Sonata #{sonata.Number} has 0 album " +
                    $"hits — track refs were resolved to the sonata instead of the leaf " +
                    $"movement, so movement-level badges show empty.");
            }
        }
    }
}
