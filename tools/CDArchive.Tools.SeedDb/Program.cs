using CDArchive.Core.Data;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Tools.SeedDb;

/// <summary>
/// Two-mode utility for the Canon data store.
///
/// <para>Default (seed) — reads the canonical JSON files and rebuilds
/// <c>data/ClassicalCanon.db</c> from scratch. Existing db file is deleted.</para>
///
/// <para><c>--export</c> — reads the SQLite database and writes the canonical
/// JSON files (composers, pieces, pick lists, albums). Useful for regenerating
/// JSON after a fresh checkout, or when the JSON files have been deleted /
/// gotten out of sync with the DB.</para>
///
/// Run from the repo root:
/// <code>
///   dotnet run --project tools/CDArchive.Tools.SeedDb            # seed
///   dotnet run --project tools/CDArchive.Tools.SeedDb -- --export # export
/// </code>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var repoRoot = FindRepoRoot();
            var dataDir  = Path.Combine(repoRoot, "data");
            var dbPath   = Path.Combine(dataDir, "ClassicalCanon.db");

            Console.WriteLine($"Data directory: {dataDir}");
            Console.WriteLine($"Target db:      {dbPath}");
            Console.WriteLine();

            return args.Contains("--export")
                ? await ExportAsync(dataDir, dbPath).ConfigureAwait(false)
                : await SeedAsync(dataDir, dbPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("OPERATION FAILED:");
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Seed: JSON → SQLite
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task<int> SeedAsync(string dataDir, string dbPath)
    {
        if (File.Exists(dbPath))
        {
            Console.WriteLine("Deleting existing database file…");
            File.Delete(dbPath);
        }

        // ── Load JSON ────────────────────────────────────────────────────
        var json = new CanonDataService(dataDir);
        Console.WriteLine("Loading composers…");
        var composers = await json.LoadComposersAsync();
        Console.WriteLine($"  {composers.Count} composers");

        Console.WriteLine("Loading pieces…");
        var pieces = await json.LoadPiecesAsync();
        Console.WriteLine($"  {pieces.Count} top-level pieces");

        Console.WriteLine("Loading pick lists…");
        var pickLists = await json.LoadPickListsAsync();

        Console.WriteLine("Loading albums…");
        var albums = await json.LoadAlbumsAsync();
        Console.WriteLine($"  {albums.Count} albums");
        Console.WriteLine();

        // ── Build context and schema ─────────────────────────────────────
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var db = new CanonDbContext(options);

        Console.WriteLine("Creating schema…");
        await db.Database.EnsureCreatedAsync();

        // ── Seed ─────────────────────────────────────────────────────────
        Console.WriteLine("Seeding…");
        var seeder = new CanonDbSeeder(db);
        var result = await seeder.SeedAsync(composers, pieces, pickLists, albums);

        Console.WriteLine();
        Console.WriteLine("── Summary ──────────────────────────────────────────────");
        Console.WriteLine(result.Summary);
        Console.WriteLine();

        // ── Report issues ────────────────────────────────────────────────
        if (result.UnknownComposerReferences.Count > 0)
        {
            Console.WriteLine("── Pieces with unknown composer references ──────────────");
            foreach (var c in result.UnknownComposerReferences.Distinct().OrderBy(n => n))
                Console.WriteLine($"  • {c}");
            Console.WriteLine();
        }

        if (result.UnresolvedRefs.Count > 0)
        {
            Console.WriteLine("── Unresolved refs by failure reason ────────────────────");
            foreach (var g in result.UnresolvedRefs
                         .GroupBy(u => u.FailureReason)
                         .OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {g.Key,-32} {g.Count(),6}");
            Console.WriteLine();

            Console.WriteLine("── Unresolved track piece-refs ──────────────────────────");
            foreach (var u in result.UnresolvedRefs)
            {
                var path = u.SubpiecePath is { Count: > 0 }
                    ? "  /  " + string.Join(" / ", u.SubpiecePath)
                    : "";
                var version = !string.IsNullOrEmpty(u.VersionDesc)
                    ? $"  [version: {u.VersionDesc}]"
                    : "";
                Console.WriteLine(
                    $"  [{u.FailureReason}]  {u.AlbumTitle}  —  D{u.DiscNumber} T{u.TrackNumber}");
                Console.WriteLine(
                    $"    {u.Composer} / {u.PieceTitle}{path}{version}");
                Console.WriteLine(
                    $"    display: {u.DisplaySummary}");
            }
            Console.WriteLine();
        }

        Console.WriteLine("Done.");
        return 0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Export: SQLite → JSON
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads everything from the SQLite database via <see cref="SqliteCanonDataService"/>
    /// and writes it to the canonical JSON files via <see cref="CanonDataService"/>.
    /// <para>
    /// The two services are deliberately decoupled: <c>SqliteCanonDataService</c>
    /// reads/writes only SQLite (no automatic JSON write-through), and
    /// <c>CanonDataService</c> reads/writes only JSON. Export is the explicit
    /// composition of "load from DB" + "save to JSON" — it never happens as a
    /// side-effect of a normal save, so JSON files can never silently diverge
    /// from the SQLite source of truth.
    /// </para>
    /// </summary>
    private static async Task<int> ExportAsync(string dataDir, string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database file not found: {dbPath}");
            Console.Error.WriteLine("Run without --export first to seed the database from JSON.");
            return 1;
        }

        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(dataDir);
        var sqlite  = new SqliteCanonDataService(factory, json);

        Console.WriteLine("Loading from SQLite…");
        var composers = await sqlite.LoadComposersAsync();
        Console.WriteLine($"  {composers.Count} composers");
        var pieces = await sqlite.LoadPiecesAsync();
        Console.WriteLine($"  {pieces.Count} top-level pieces");
        var pickLists = await sqlite.LoadPickListsAsync();
        var albums = await sqlite.LoadAlbumsAsync();
        Console.WriteLine($"  {albums.Count} albums");
        Console.WriteLine();

        // Write through CanonDataService directly — SqliteCanonDataService no
        // longer touches JSON, so the export composes the two services explicitly.
        Console.WriteLine("Writing JSON files…");
        await json.SaveComposersAsync(composers);
        Console.WriteLine($"  {json.ComposersFilePath}");
        await json.SavePickListsAsync(pickLists);
        Console.WriteLine($"  {json.PickListsFilePath}");
        await json.SavePiecesAsync(pieces);
        Console.WriteLine($"  {json.PiecesFilePath}");
        await json.SaveAlbumsAsync(albums);
        Console.WriteLine($"  {json.AlbumsFilePath}");
        Console.WriteLine();

        Console.WriteLine("Done.");
        return 0;
    }

    private sealed class SimpleDbContextFactory : IDbContextFactory<CanonDbContext>
    {
        private readonly DbContextOptions<CanonDbContext> _options;
        public SimpleDbContextFactory(DbContextOptions<CanonDbContext> options) => _options = options;
        public CanonDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Walks up from the executing assembly's directory to find the repo root.
    /// Accepts either <c>Classical Canon composers.json</c> or <c>ClassicalCanon.db</c>
    /// as the marker so the tool works post-migration even when JSON files
    /// have been deleted (export mode regenerates them).
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var data = Path.Combine(dir.FullName, "data");
            if (Directory.Exists(data) &&
                (File.Exists(Path.Combine(data, "Classical Canon composers.json")) ||
                 File.Exists(Path.Combine(data, "ClassicalCanon.db"))))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate repo root (walked up from " +
            AppContext.BaseDirectory + " looking for data/Classical Canon composers.json " +
            "or data/ClassicalCanon.db).");
    }
}
