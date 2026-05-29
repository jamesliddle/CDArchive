using System.Text.Json;
using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// Coverage for the track-level <c>IsStereo</c> field added to
/// <see cref="AlbumTrack"/> in the post-2026-05-13 work. Pins the JSON
/// serialization contract (snake_case <c>"stereo"</c>, omitted when null)
/// and verifies that all three states (null / true / false) survive a
/// SQLite save+load round-trip.
/// </summary>
public class AlbumTrackIsStereoTests
{
    private static SqliteCanonDataService NewServiceOnFreshDb(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_track_stereo_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath};Foreign Keys=True")
            .Options;
        var factory = new SimpleDbContextFactory(options);
        var json    = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    // ── JSON contract ────────────────────────────────────────────────────

    [Fact]
    public void Json_NullIsStereo_OmitsProperty()
    {
        var t = new AlbumTrack { TrackNumber = 1 };
        var json = JsonSerializer.Serialize(t);
        Assert.DoesNotContain("\"stereo\"", json);
    }

    [Fact]
    public void Json_TrueIsStereo_SerializesAsStereoTrue()
    {
        var t = new AlbumTrack { TrackNumber = 1, IsStereo = true };
        var json = JsonSerializer.Serialize(t);
        // The JSON key is snake-case "stereo", not "is_stereo", because the
        // legacy serialization shape for the matching album-level field used
        // that key and tracks adopted the same convention.
        Assert.Contains("\"stereo\":true", json);
    }

    [Fact]
    public void Json_FalseIsStereo_SerializesAsStereoFalse()
    {
        var t = new AlbumTrack { TrackNumber = 1, IsStereo = false };
        var json = JsonSerializer.Serialize(t);
        Assert.Contains("\"stereo\":false", json);
    }

    [Fact]
    public void Json_RoundTrip_PreservesAllThreeStates()
    {
        foreach (var state in new bool?[] { null, true, false })
        {
            var t = new AlbumTrack { TrackNumber = 1, IsStereo = state };
            var json = JsonSerializer.Serialize(t);
            var back = JsonSerializer.Deserialize<AlbumTrack>(json)!;
            Assert.Equal(state, back.IsStereo);
        }
    }

    [Fact]
    public void Json_MissingProperty_DeserializesAsNull()
    {
        var json = "{\"track\":1}";
        var t = JsonSerializer.Deserialize<AlbumTrack>(json)!;
        Assert.Null(t.IsStereo);
    }

    // ── SQLite round-trip ────────────────────────────────────────────────

    [Fact]
    public async Task Sqlite_RoundTrip_PreservesAllThreeStates()
    {
        var dbPath = "";
        try
        {
            var svc = NewServiceOnFreshDb(out dbPath);

            // Minimal canon for the album to reference.
            var composer = new CanonComposer
            {
                Name     = "Beethoven, Ludwig van",
                SortName = "Beethoven, Ludwig van",
            };
            await svc.SaveComposersAsync(new List<CanonComposer> { composer });

            var album = new CanonAlbum
            {
                Title = "Stereo Round-Trip Fixture",
                Discs = [new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks =
                    [
                        new AlbumTrack { TrackNumber = 1, IsStereo = true  },
                        new AlbumTrack { TrackNumber = 2, IsStereo = false },
                        new AlbumTrack { TrackNumber = 3, IsStereo = null  },
                    ],
                }],
            };
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            var loaded = await svc.LoadAlbumsAsync();
            var only = Assert.Single(loaded);
            var tracks = only.Discs[0].Tracks;
            Assert.Equal(3, tracks.Count);
            Assert.True(tracks[0].IsStereo);
            Assert.False(tracks[1].IsStereo);
            Assert.Null(tracks[2].IsStereo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    /// <summary>
    /// Updating a track's IsStereo from null to true (and back to null) must
    /// persist through SQLite — the dedup path mustn't clobber the new value.
    /// </summary>
    [Fact]
    public async Task Sqlite_UpdateExistingTrack_PersistsIsStereoTransitions()
    {
        var dbPath = "";
        try
        {
            var svc = NewServiceOnFreshDb(out dbPath);
            await svc.SaveComposersAsync(new List<CanonComposer>
            {
                new() { Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van" },
            });

            var album = new CanonAlbum
            {
                Title = "Stereo Transition Fixture",
                Discs = [new AlbumDisc
                {
                    DiscNumber = 1,
                    Tracks = [ new AlbumTrack { TrackNumber = 1 } ], // IsStereo = null
                }],
            };
            await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

            // null → true
            var loaded = await svc.LoadAlbumsAsync();
            loaded[0].Discs[0].Tracks[0].IsStereo = true;
            await svc.SaveAlbumsAsync(loaded);
            loaded = await svc.LoadAlbumsAsync();
            Assert.True(loaded[0].Discs[0].Tracks[0].IsStereo);

            // true → false
            loaded[0].Discs[0].Tracks[0].IsStereo = false;
            await svc.SaveAlbumsAsync(loaded);
            loaded = await svc.LoadAlbumsAsync();
            Assert.False(loaded[0].Discs[0].Tracks[0].IsStereo);

            // false → null
            loaded[0].Discs[0].Tracks[0].IsStereo = null;
            await svc.SaveAlbumsAsync(loaded);
            loaded = await svc.LoadAlbumsAsync();
            Assert.Null(loaded[0].Discs[0].Tracks[0].IsStereo);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (!string.IsNullOrEmpty(dbPath) && File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }
}
