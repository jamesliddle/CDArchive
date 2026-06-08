using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers slice 1 of the MusicBrainz import-integration:
/// <see cref="CanonComposer.MusicBrainzArtistId"/>,
/// <see cref="CanonPiece.MusicBrainzWorkId"/>, and
/// <see cref="CanonAlbum.MusicBrainzReleaseId"/>.
///
/// <list type="bullet">
///   <item><b>JSON round-trip.</b> The fields serialise and deserialise via
///         <c>System.Text.Json</c> under the documented snake_case keys,
///         and are omitted from output when null (so the user's JSON
///         snapshots don't bloat with empty MBIDs).</item>
///   <item><b>SQLite round-trip.</b> Save→Load via <see cref="SqliteCanonDataService"/>
///         preserves the value for each of the three subsystems.</item>
///   <item><b>Schema migration.</b> A legacy DB that lacks the columns
///         gets them added on first <see cref="SqliteCanonDataService.LoadComposersAsync"/>
///         (or sibling). The migration is idempotent on a fresh DB.</item>
/// </list>
/// </summary>
public class MusicBrainzIdRoundTripTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    // Realistic 36-char UUIDs — same shape MB returns. Distinct values per
    // entity kind so a mis-wired mapper (e.g. accidentally copying composer
    // → piece) shows up immediately.
    private const string ArtistMbid  = "1f9df192-a621-4f54-8850-2c5373b7eac9"; // Beethoven
    private const string WorkMbid    = "8c6a91a3-9f29-4bba-a8f5-d2e93b9f7d2c"; // canonical work
    private const string ReleaseMbid = "3e0c2f88-9bf9-4f4a-bd95-b6f9b87d3a8e"; // a release

    public MusicBrainzIdRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CDArchive.Tests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "ClassicalCanon.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private SqliteCanonDataService NewService()
    {
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={_dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(_tempDir);
        return new SqliteCanonDataService(factory, json);
    }

    // ── JSON round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void Composer_MusicBrainzArtistId_RoundTripsThroughJson()
    {
        var src = new CanonComposer { Name = "Beethoven", MusicBrainzArtistId = ArtistMbid };
        var json = JsonSerializer.Serialize(src);
        Assert.Contains("\"musicbrainz_artist_id\":\"" + ArtistMbid + "\"", json);

        var round = JsonSerializer.Deserialize<CanonComposer>(json)!;
        Assert.Equal(ArtistMbid, round.MusicBrainzArtistId);
    }

    [Fact]
    public void Piece_MusicBrainzWorkId_RoundTripsThroughJson()
    {
        var src = new CanonPiece { Composer = "Beethoven", Title = "Sonata", MusicBrainzWorkId = WorkMbid };
        var json = JsonSerializer.Serialize(src);
        Assert.Contains("\"musicbrainz_work_id\":\"" + WorkMbid + "\"", json);

        var round = JsonSerializer.Deserialize<CanonPiece>(json)!;
        Assert.Equal(WorkMbid, round.MusicBrainzWorkId);
    }

    [Fact]
    public void Album_MusicBrainzReleaseId_RoundTripsThroughJson()
    {
        var src = new CanonAlbum { Title = "Choral Fantasy", MusicBrainzReleaseId = ReleaseMbid };
        var json = JsonSerializer.Serialize(src);
        Assert.Contains("\"musicbrainz_release_id\":\"" + ReleaseMbid + "\"", json);

        var round = JsonSerializer.Deserialize<CanonAlbum>(json)!;
        Assert.Equal(ReleaseMbid, round.MusicBrainzReleaseId);
    }

    [Fact]
    public void NullMbids_AreOmittedFromJson_KeepingSnapshotsClean()
    {
        // The [JsonIgnore(WhenWritingNull)] attribute keeps the new fields
        // out of the user's JSON snapshots when unset — otherwise every
        // existing row would bloat with empty MBIDs on the next --export.
        var composer = JsonSerializer.Serialize(new CanonComposer { Name = "X" });
        var piece    = JsonSerializer.Serialize(new CanonPiece    { Title = "X" });
        var album    = JsonSerializer.Serialize(new CanonAlbum    { Title = "X" });

        Assert.DoesNotContain("musicbrainz_artist_id",  composer);
        Assert.DoesNotContain("musicbrainz_work_id",    piece);
        Assert.DoesNotContain("musicbrainz_release_id", album);
    }

    // ── SQLite round-trip ────────────────────────────────────────────────────

    [Fact]
    public async Task Composer_MusicBrainzArtistId_RoundTripsThroughSqlite()
    {
        var svc = NewService();
        var c = new CanonComposer
        {
            Name = "Beethoven, Ludwig van",
            SortName = "Beethoven, Ludwig van",
            MusicBrainzArtistId = ArtistMbid,
        };
        await svc.SaveComposersAsync([c]);

        // Re-read via a fresh service so we exercise the LoadComposersAsync
        // mapper (rather than getting back the in-memory model we just saved).
        var reload = (await NewService().LoadComposersAsync()).Single();
        Assert.Equal(ArtistMbid, reload.MusicBrainzArtistId);

        // Round-trip a null too — make sure a previously-set MBID can be cleared.
        reload.MusicBrainzArtistId = null;
        await NewService().SaveComposersAsync([reload]);

        var reload2 = (await NewService().LoadComposersAsync()).Single();
        Assert.Null(reload2.MusicBrainzArtistId);
    }

    [Fact]
    public async Task Piece_MusicBrainzWorkId_RoundTripsThroughSqlite()
    {
        var svc = NewService();
        await svc.SaveComposersAsync([
            new CanonComposer { Name = "Beethoven", SortName = "Beethoven" }
        ]);

        var p = new CanonPiece
        {
            Composer = "Beethoven",
            Title    = "Piano Sonata #14",
            MusicBrainzWorkId = WorkMbid,
        };
        await svc.SavePiecesAsync([p]);

        var reload = (await NewService().LoadPiecesAsync()).Single();
        Assert.Equal(WorkMbid, reload.MusicBrainzWorkId);
    }

    [Fact]
    public async Task Album_MusicBrainzReleaseId_RoundTripsThroughSqlite()
    {
        var svc = NewService();
        var a = new CanonAlbum
        {
            Title = "Choral Fantasy",
            MusicBrainzReleaseId = ReleaseMbid,
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { new AlbumTrack { TrackNumber = 1 } } } },
        };
        await svc.SaveAlbumsAsync([a]);

        var reload = (await NewService().LoadAlbumsAsync()).Single();
        Assert.Equal(ReleaseMbid, reload.MusicBrainzReleaseId);
    }

    // ── Schema migration ─────────────────────────────────────────────────────

    [Fact]
    public async Task FreshDb_HasAllThreeMbidColumns_AndMigrationIsIdempotent()
    {
        // Trigger schema init via any Load call.
        await NewService().LoadComposersAsync();

        Assert.True(await ColumnExistsAsync("composers", "musicbrainz_artist_id"));
        Assert.True(await ColumnExistsAsync("pieces",    "musicbrainz_work_id"));
        Assert.True(await ColumnExistsAsync("albums",    "musicbrainz_release_id"));

        // Re-running init via a fresh service must no-op (the EnsureColumnAsync
        // helper checks PRAGMA table_info before issuing the ALTER). If it
        // weren't idempotent, the second call would raise SqliteException
        // "duplicate column name".
        await NewService().LoadComposersAsync();
        await NewService().LoadComposersAsync();
        // No assertion needed beyond "didn't throw" — the column-exists
        // assertions above already pin the post-state.
    }

    [Fact]
    public async Task LegacyDb_WithoutMbidColumns_GetsThemAddedOnInit()
    {
        // Stand up the modern schema, then drop the three new columns to
        // simulate a DB last opened by a pre-MB-integration build. SQLite
        // can't DROP COLUMN before 3.35; we build a synthetic legacy DB by
        // raw CREATE TABLE in the pre-fix shape, dropping the FK-dependent
        // children first so the DROP succeeds.
        await NewService().LoadComposersAsync();

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            // Composers: drop the new column. SQLite 3.35+ supports DROP COLUMN;
            // Microsoft.Data.Sqlite ships a bundled version that supports it.
            await ExecAsync(conn, "ALTER TABLE composers DROP COLUMN musicbrainz_artist_id");
            await ExecAsync(conn, "ALTER TABLE pieces    DROP COLUMN musicbrainz_work_id");
            await ExecAsync(conn, "ALTER TABLE albums    DROP COLUMN musicbrainz_release_id");
        }

        // Sanity: the columns are really gone.
        Assert.False(await ColumnExistsAsync("composers", "musicbrainz_artist_id"));
        Assert.False(await ColumnExistsAsync("pieces",    "musicbrainz_work_id"));
        Assert.False(await ColumnExistsAsync("albums",    "musicbrainz_release_id"));

        // New service triggers schema init again → the migration re-adds them.
        await NewService().LoadComposersAsync();

        Assert.True(await ColumnExistsAsync("composers", "musicbrainz_artist_id"));
        Assert.True(await ColumnExistsAsync("pieces",    "musicbrainz_work_id"));
        Assert.True(await ColumnExistsAsync("albums",    "musicbrainz_release_id"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
