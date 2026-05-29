using CDArchive.Core.Data;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace CDArchive.Core.Tests;

/// <summary>
/// H21 slice 1 regression tests for the stable-Id session reference
/// (<see cref="RecordingSession.Id"/> + <see cref="AlbumTrack.SessionId"/>)
/// living alongside the legacy positional <see cref="AlbumTrack.SessionIndex"/>.
///
/// <para>The save path should:</para>
/// <list type="bullet">
///   <item>Populate <see cref="RecordingSession.Id"/> on load (from SQLite PK).</item>
///   <item>Populate <see cref="AlbumTrack.SessionId"/> on load alongside
///         <see cref="AlbumTrack.SessionIndex"/>.</item>
///   <item>Prefer <see cref="AlbumTrack.SessionId"/> on save when set, falling
///         back to the legacy positional <see cref="AlbumTrack.SessionIndex"/>
///         for pre-H21 snapshots and freshly-added in-memory sessions.</item>
/// </list>
/// </summary>
public class SessionStableIdTests
{
    private static SqliteCanonDataService NewService(out string dbPath, out IDbContextFactory<CanonDbContext> factory)
    {
        dbPath = Path.Combine(Path.GetTempPath(),
            $"cdarchive_session_stableid_{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<CanonDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        factory = new SimpleDbContextFactory(options);
        var json = new CanonDataService(Path.GetTempPath());
        return new SqliteCanonDataService(factory, json);
    }

    private static CanonAlbum BuildAlbumWithTwoSessions()
    {
        return new CanonAlbum
        {
            Title = "Symphony Cycle",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "March 1967", Venue = "Jesus-Christus-Kirche" },
                new() { Dates = "November 1968", Venue = "Philharmonie" },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, Description = "Symphony I",  SessionIndex = 0 },
                        new() { TrackNumber = 2, Description = "Symphony II", SessionIndex = 1 },
                    },
                },
            },
        };
    }

    [Fact]
    public async Task Load_PopulatesSessionIdOnRecordingSessionAndOnTrack()
    {
        var svc = NewService(out _, out _);
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { BuildAlbumWithTwoSessions() });

        var loaded = await svc.LoadAlbumsAsync();
        var album  = Assert.Single(loaded);

        Assert.NotNull(album.Sessions);
        Assert.Equal(2, album.Sessions!.Count);
        Assert.All(album.Sessions, s => Assert.NotEqual(0L, s.Id));

        var tracks = album.Discs[0].Tracks;
        Assert.NotNull(tracks[0].SessionId);
        Assert.NotNull(tracks[1].SessionId);

        // SessionId on each track matches the corresponding session's Id.
        Assert.Equal(album.Sessions[0].Id, tracks[0].SessionId);
        Assert.Equal(album.Sessions[1].Id, tracks[1].SessionId);

        // SessionIndex is no longer populated by the load path — the model's
        // SessionIndex field is now a transient in-memory handle for the
        // in-editor unsaved-session case only.
        Assert.Null(tracks[0].SessionIndex);
        Assert.Null(tracks[1].SessionIndex);
    }

    [Fact]
    public async Task RoundTrip_PreservesStableSessionIdsAcrossEdit()
    {
        var svc = NewService(out _, out var factory);
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { BuildAlbumWithTwoSessions() });

        var firstLoad = (await svc.LoadAlbumsAsync()).Single();
        var session0Id = firstLoad.Sessions![0].Id;
        var session1Id = firstLoad.Sessions[1].Id;

        // Mutate a scalar so the save path runs end-to-end, then re-save.
        firstLoad.Title = "Symphony Cycle — Reissue";
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { firstLoad });

        // Underlying AlbumSessionRow IDs survive the round-trip.
        await using var db = await factory.CreateDbContextAsync();
        var sessionRowIds = await db.AlbumSessions
            .OrderBy(s => s.Position)
            .Select(s => s.Id)
            .ToListAsync();
        Assert.Equal(new[] { session0Id, session1Id }, sessionRowIds);
    }

    [Fact]
    public async Task Save_PrefersTrackSessionIdOverSessionIndex_WhenBothPresent()
    {
        var svc = NewService(out _, out var factory);
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { BuildAlbumWithTwoSessions() });

        var loaded = (await svc.LoadAlbumsAsync()).Single();
        var session0Id = loaded.Sessions![0].Id;
        var session1Id = loaded.Sessions[1].Id;

        // Simulate the "stable Id wins" contract: set SessionId to session1Id
        // but leave SessionIndex pointing at the OLD value (0). After save the
        // track's Session FK should match session1, not session0.
        var track0 = loaded.Discs[0].Tracks[0];
        track0.SessionId    = session1Id;
        track0.SessionIndex = 0;     // legacy value — should be ignored

        await svc.SaveAlbumsAsync(new List<CanonAlbum> { loaded });

        await using var db = await factory.CreateDbContextAsync();
        var trackRow = await db.AlbumTracks
            .Include(t => t.Disc).ThenInclude(d => d!.Album)
            .SingleAsync(t => t.TrackNumber == 1);
        Assert.Equal(session1Id, trackRow.SessionId);
    }

    [Fact]
    public async Task Save_FallsBackToSessionIndex_WhenSessionIdNotSet()
    {
        // Pre-H21 snapshot: SessionIndex set, SessionId null. The save path
        // must still resolve correctly via the positional fallback.
        var album = new CanonAlbum
        {
            Title = "Pre-H21 album",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "1965" },
                new() { Dates = "1972" },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        // SessionId deliberately null, only SessionIndex set.
                        new() { TrackNumber = 1, SessionIndex = 1, SessionId = null },
                    },
                },
            },
        };

        var svc = NewService(out _, out _);
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loaded = (await svc.LoadAlbumsAsync()).Single();
        var track  = loaded.Discs[0].Tracks[0];

        // Pre-H21 input → SessionIndex resolved on save → reload populates
        // SessionId (the canonical post-H21 reference). SessionIndex is not
        // populated by load anymore.
        Assert.Null(track.SessionIndex);
        Assert.Equal(loaded.Sessions![1].Id, track.SessionId);
    }

    [Fact]
    public async Task Save_AddSessionToExistingAlbum_BackPropagatesIdsToInMemoryModel()
    {
        // Without back-propagation, the post-save in-memory model still has
        // newSession.Id = 0 and Track2.SessionId = null. The next operation
        // that touches the model (e.g. the AlbumEditor JSON-cloning the album
        // for a second edit pass) loses the session reference because
        // SessionIndex is [JsonIgnore] and SessionId is null/0.
        //
        // The data service must update the in-memory model after SaveChanges
        // so the next pass sees the persistent state.
        var svc = NewService(out _, out _);

        var initial = new CanonAlbum
        {
            Title = "Box set",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "Session A" },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, SessionIndex = 0 },
                        new() { TrackNumber = 2 },
                    },
                },
            },
        };
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { initial });

        var loaded = (await svc.LoadAlbumsAsync()).Single();
        loaded.Sessions!.Add(new RecordingSession { Dates = "Session B" });
        var track2 = loaded.Discs[0].Tracks.Single(t => t.TrackNumber == 2);
        track2.SessionId    = null;
        track2.SessionIndex = 1;

        await svc.SaveAlbumsAsync(new List<CanonAlbum> { loaded });

        // After save, the IN-MEMORY model must reflect the persistent state.
        var newSessionInMemory = loaded.Sessions[1];
        Assert.NotEqual(0L, newSessionInMemory.Id);    // Id allocated by SQLite
        Assert.Equal(newSessionInMemory.Id, track2.SessionId);   // track points at it
        Assert.Null(track2.SessionIndex);              // transient handle cleared
    }

    [Fact]
    public async Task Save_AddSessionToExistingAlbum_ViaJsonClone_PreservesLinkAcrossReload()
    {
        // Closer to the actual editor flow: the AlbumEditor JSON-clones the
        // input album at open time, the user edits the clone, the clone is
        // substituted back into AllAlbums on OK. The clone is a different
        // CanonAlbum instance from the original, so the data service's CWT
        // identity tracking misses and falls back to IdentityKey lookup.
        var svc = NewService(out _, out _);

        var initial = new CanonAlbum
        {
            Title = "Box set",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "Session A" },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, SessionIndex = 0 },
                        new() { TrackNumber = 2 },
                    },
                },
            },
        };
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { initial });

        // Step 1: load the album (this is what AlbumsViewModel.LoadDataAsync does).
        var loaded = (await svc.LoadAlbumsAsync()).Single();

        // Step 2: JSON-clone the album, simulating the AlbumEditor's open-time clone.
        var json  = System.Text.Json.JsonSerializer.Serialize(loaded);
        var clone = System.Text.Json.JsonSerializer.Deserialize<CanonAlbum>(json)!;

        // Step 3: simulate the editor flow on the clone.
        clone.Sessions!.Add(new RecordingSession { Dates = "Session B" /* Id=0 */ });
        var cloneTrack2 = clone.Discs[0].Tracks.Single(t => t.TrackNumber == 2);
        cloneTrack2.SessionId    = null;
        cloneTrack2.SessionIndex = 1;

        // Step 4: save the clone (this is what happens after AlbumsView substitutes
        // vm.AllAlbums[idx] = result).
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { clone });

        // Step 5: reload.
        var reloaded = (await svc.LoadAlbumsAsync()).Single();

        Assert.Equal(2, reloaded.Sessions!.Count);
        var sessionB = reloaded.Sessions[1];
        Assert.Equal("Session B", sessionB.Dates);

        var reloadedTrack2 = reloaded.Discs[0].Tracks.Single(t => t.TrackNumber == 2);
        Assert.Equal(sessionB.Id, reloadedTrack2.SessionId);   // THE REGRESSION
    }

    [Fact]
    public async Task Save_AddSessionToExistingAlbum_LinkTrackToIt_PreservesLinkAcrossReload()
    {
        // User-reported regression scenario:
        //   1. Open an existing album that already has sessions.
        //   2. Add a new session via the AlbumEditor.
        //   3. Pick the new session for a track in the TrackEditor.
        //   4. Save the album.
        //   5. Re-open it — the track's link to the new session should
        //      survive. Pre-fix it didn't.
        var svc = NewService(out _, out _);

        // Step 1: pre-existing album with one session and one session-bearing
        // track.
        var initial = new CanonAlbum
        {
            Title = "Box set",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "Session A" },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, SessionIndex = 0 },
                        new() { TrackNumber = 2 },   // no session yet
                    },
                },
            },
        };
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { initial });

        // Step 2: simulate the editor flow — load, then add a new session,
        // then link track 2 to it.
        var loaded = (await svc.LoadAlbumsAsync()).Single();
        loaded.Sessions!.Add(new RecordingSession { Dates = "Session B" /* Id=0 */ });
        var track2 = loaded.Discs[0].Tracks.Single(t => t.TrackNumber == 2);
        // VM behaviour: SessionId stays null (Id=0 isn't a real Id) and
        // SessionIndex carries the in-memory position of the new session.
        track2.SessionId    = null;
        track2.SessionIndex = 1;

        // Step 3: save.
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { loaded });

        // Step 4: reload — the new session should exist AND track 2 should
        // still point at it.
        var reloaded = (await svc.LoadAlbumsAsync()).Single();

        Assert.Equal(2, reloaded.Sessions!.Count);
        var sessionA = reloaded.Sessions[0];
        var sessionB = reloaded.Sessions[1];
        Assert.Equal("Session A", sessionA.Dates);
        Assert.Equal("Session B", sessionB.Dates);

        var reloadedTrack1 = reloaded.Discs[0].Tracks.Single(t => t.TrackNumber == 1);
        var reloadedTrack2 = reloaded.Discs[0].Tracks.Single(t => t.TrackNumber == 2);

        Assert.Equal(sessionA.Id, reloadedTrack1.SessionId);
        Assert.Equal(sessionB.Id, reloadedTrack2.SessionId);   // ← the regression
    }

    [Fact]
    public async Task Save_FreshlyAddedSessionWithoutId_StillResolvesViaSessionIndex()
    {
        // A user-added session in the editor has Id = 0 until SQLite assigns
        // one on save. Tracks reference it via SessionIndex during that turn —
        // the positional fallback must keep working.
        var album = new CanonAlbum
        {
            Title = "Freshly built",
            Sessions = new List<RecordingSession>
            {
                new() { Dates = "fresh-1" /* Id stays 0 */ },
                new() { Dates = "fresh-2" /* Id stays 0 */ },
            },
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new() { TrackNumber = 1, SessionIndex = 1 /* SessionId null */ },
                    },
                },
            },
        };

        var svc = NewService(out _, out _);
        await svc.SaveAlbumsAsync(new List<CanonAlbum> { album });

        var loaded = (await svc.LoadAlbumsAsync()).Single();
        Assert.Equal(loaded.Sessions![1].Id, loaded.Discs[0].Tracks[0].SessionId);
    }
}
