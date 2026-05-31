using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// End-to-end coverage for loose tracks (singletons with no owning album).
/// Each test gets a fresh temp DB. Covers the four contract points:
///
/// <list type="bullet">
///   <item>Performers table migration: legacy <c>album_performers.album_id NOT NULL</c>
///     → nullable, preserving every existing row.</item>
///   <item>Round-trip: a loose track with scalar fields, piece refs, and
///     track-level performers saves and loads back byte-for-byte.</item>
///   <item>Orphan delete: a loose track removed from the input list is
///     deleted from the DB on the next save.</item>
///   <item>Coexistence: loose tracks and album-bound tracks share the same
///     table; saving / loading one doesn't perturb the other.</item>
/// </list>
/// </summary>
public class LooseTrackRoundTripTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly SqliteCanonDataService _svc;

    public LooseTrackRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "ClassicalCanon.db");

        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        _svc = new SqliteCanonDataService(factory, json);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private SqliteCanonDataService CreateFreshService()
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        return new SqliteCanonDataService(factory, json);
    }

    /// <summary>
    /// Legacy DB: hand-build album_performers with NOT NULL album_id, seed a
    /// row anchored on an album, then migrate. After migration: column is
    /// nullable, the existing row survived with its id and field values, and
    /// the new <c>ck_album_performers_has_owner</c> CHECK rejects rows with
    /// both album_id and track_id NULL.
    /// </summary>
    [Fact]
    public async Task LegacyPerformersDb_MigratesToNullableAlbumId_AndPreservesRows()
    {
        await _svc.LoadAlbumsAsync();   // initial schema creation.

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await Exec(conn, "PRAGMA foreign_keys=OFF");
            await Exec(conn, "DROP TABLE album_performers");
            // Pre-migration shape: album_id NOT NULL, no ck_album_performers_has_owner.
            await Exec(conn, """
                CREATE TABLE album_performers (
                    id           INTEGER NOT NULL CONSTRAINT PK_album_performers PRIMARY KEY AUTOINCREMENT,
                    album_id     INTEGER NOT NULL,
                    track_id     INTEGER     NULL,
                    position     INTEGER NOT NULL,
                    person_id    INTEGER     NULL,
                    ensemble_id  INTEGER     NULL,
                    display_name TEXT        NULL,
                    role         TEXT        NULL,
                    instrument   TEXT        NULL
                )
                """);
            await Exec(conn, """
                INSERT INTO albums (id, title, is_provisional) VALUES (1, 'Seed Album', 0)
                """);
            await Exec(conn, """
                INSERT INTO album_performers (id, album_id, track_id, position, display_name, role)
                VALUES (501, 1, NULL, 0, 'Vladimir Horowitz', 'piano')
                """);
            await Exec(conn, "PRAGMA foreign_keys=ON");
        }

        Assert.True(await ColumnIsNotNullAsync("album_performers", "album_id"),
            "Test setup precondition: legacy album_performers should have NOT NULL album_id.");

        // Migration kicks in on the fresh service.
        await CreateFreshService().LoadAlbumsAsync();

        Assert.False(await ColumnIsNotNullAsync("album_performers", "album_id"),
            "After migration, album_id should be nullable.");

        // Existing row survived with its id and content.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT id, album_id, display_name, role FROM album_performers WHERE id = 501";
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(501L,                reader.GetInt64(0));
            Assert.Equal(1L,                  reader.GetInt64(1));
            Assert.Equal("Vladimir Horowitz", reader.GetString(2));
            Assert.Equal("piano",             reader.GetString(3));
        }

        // ck_album_performers_has_owner rejects a row with both FKs null.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            var ex = await Assert.ThrowsAsync<SqliteException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO album_performers (album_id, track_id, position, display_name)
                    VALUES (NULL, NULL, 0, 'Floating Credit')
                    """;
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Contains("ck_album_performers_has_owner", ex.Message);
        }
    }

    /// <summary>
    /// Round-trip a loose track carrying every scalar field plus piece refs
    /// and track-level performers. Seed Beethoven + a piece so the piece-ref
    /// resolves on save and reconstitutes on load.
    /// </summary>
    [Fact]
    public async Task LooseTrack_RoundTripsScalarsRefsAndPerformers()
    {
        // Seed canon so refs resolve.
        await _svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van", IsProvisional = false },
        });
        await _svc.SavePiecesAsync(new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Für Elise", IsProvisional = false },
        });

        var loose = new AlbumTrack
        {
            TrackNumber   = 0,
            Description   = "Standalone Für Elise download",
            Duration      = "2:54",
            SparsCode     = "DDD",
            IsStereo      = true,
            IsProvisional = true,
            FlacPath      = @"C:\Users\james\Music\loose\fur-elise.flac",
            Mp3Path       = @"C:\Users\james\Music\loose\fur-elise.mp3",
            PieceRefs     =
            [
                new TrackPieceRef
                {
                    Composer   = "Beethoven, Ludwig van",
                    PieceTitle = "Für Elise",
                },
            ],
            Performers =
            [
                new AlbumPerformer { Name = "Lang Lang",        Role = "piano" },
                new AlbumPerformer { Name = "Studio recording", Role = "venue" },
            ],
        };

        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { loose });

        // Reload through a fresh service so we exercise the read path against
        // SQLite, not the in-memory CWT identity that the save path uses.
        var fresh = CreateFreshService();
        var loaded = await fresh.LoadLooseTracksAsync();

        var got = Assert.Single(loaded);
        Assert.Equal("Standalone Für Elise download",            got.Description);
        Assert.Equal("2:54",                                     got.Duration);
        Assert.Equal("DDD",                                      got.SparsCode);
        Assert.True(got.IsStereo);
        Assert.True(got.IsProvisional);
        Assert.Equal(@"C:\Users\james\Music\loose\fur-elise.flac", got.FlacPath);
        Assert.Equal(@"C:\Users\james\Music\loose\fur-elise.mp3",  got.Mp3Path);
        // Session fields stay null on loose tracks (no parent album to default from).
        Assert.Null(got.SessionDates);
        Assert.Null(got.SessionVenue);

        var pieceRef = Assert.Single(got.PieceRefs!);
        Assert.Equal("Beethoven, Ludwig van", pieceRef.Composer);
        Assert.Equal("Für Elise",             pieceRef.PieceTitle);

        Assert.NotNull(got.Performers);
        Assert.Equal(2, got.Performers!.Count);
        Assert.Equal("Lang Lang",        got.Performers[0].Name);
        Assert.Equal("piano",            got.Performers[0].Role);
        Assert.Equal("Studio recording", got.Performers[1].Name);
        Assert.Equal("venue",            got.Performers[1].Role);

        // Sanity: the row carries disc_id = NULL.
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM album_tracks WHERE disc_id IS NULL";
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    /// <summary>
    /// A loose track removed from the input list is orphan-deleted from the
    /// DB on the next save — same semantic SaveAlbumsAsync gives for the
    /// album-bound case.
    /// </summary>
    [Fact]
    public async Task LooseTrack_RemovedFromInput_IsDeletedFromDb()
    {
        await _svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van", IsProvisional = false },
        });

        var first  = new AlbumTrack { Description = "A", IsProvisional = true };
        var second = new AlbumTrack { Description = "B", IsProvisional = true };
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { first, second });

        // Drop the second one. Identity is tracked via CWT — the kept track
        // matches its row, the dropped one becomes an orphan.
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack> { first });

        var loaded = await CreateFreshService().LoadLooseTracksAsync();
        var only = Assert.Single(loaded);
        Assert.Equal("A", only.Description);
    }

    /// <summary>
    /// Loose tracks and album-bound tracks share <c>album_tracks</c>. A save
    /// of one type must not perturb the other type.
    /// </summary>
    [Fact]
    public async Task LooseTracks_AndAlbumTracks_Coexist()
    {
        await _svc.SaveComposersAsync(new List<CanonComposer>
        {
            new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van", IsProvisional = false },
        });

        // Album with one track.
        var album = new CanonAlbum
        {
            Title = "Op. 27 No. 2",
            Discs =
            {
                new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    {
                        new AlbumTrack { TrackNumber = 1, Description = "Moonlight: I. Adagio sostenuto" },
                    },
                },
            },
        };
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        // Loose track in the same DB.
        await _svc.SaveLooseTracksAsync(new List<AlbumTrack>
        {
            new() { Description = "Some download", IsProvisional = true },
        });

        // Re-save the album with no change: the loose track must still be there.
        await _svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var fresh = CreateFreshService();
        var albums      = await fresh.LoadAlbumsAsync();
        var looseTracks = await fresh.LoadLooseTracksAsync();

        Assert.Single(albums);
        Assert.Single(albums[0].Discs[0].Tracks);
        Assert.Single(looseTracks);

        // And re-saving the loose track must not perturb the album.
        await fresh.SaveLooseTracksAsync(looseTracks);
        var albumsAfter = await CreateFreshService().LoadAlbumsAsync();
        Assert.Single(albumsAfter);
        Assert.Single(albumsAfter[0].Discs[0].Tracks);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task<bool> ColumnIsNotNullAsync(string table, string column)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return reader.GetInt32(3) == 1;
        }
        throw new InvalidOperationException($"Column {table}.{column} not found.");
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
