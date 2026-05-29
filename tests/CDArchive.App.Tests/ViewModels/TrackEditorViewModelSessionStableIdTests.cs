using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H21 architectural slice 2: <see cref="TrackEditorViewModel"/> now writes
/// <see cref="AlbumTrack.SessionId"/> on save (alongside the legacy positional
/// <see cref="AlbumTrack.SessionIndex"/>), so the user picking a session in
/// the UI sticks to a stable identity rather than a position that can shift.
///
/// <para>Slice 1's SQLite layer prefers <see cref="AlbumTrack.SessionId"/>
/// when set — that hand-off is what these tests anchor.</para>
/// </summary>
public class TrackEditorViewModelSessionStableIdTests
{
    private static IList<RecordingSession> TwoSessions(long id0 = 100, long id1 = 200) =>
        new List<RecordingSession>
        {
            new() { Id = id0, Dates = "session 0" },
            new() { Id = id1, Dates = "session 1" },
        };

    // ── Load: prefer SessionId over SessionIndex ─────────────────────────────

    [Fact]
    public void LoadSingle_WithSessionIdAndSessions_ResolvesPositionFromId()
    {
        var sessions = TwoSessions();
        var track = new AlbumTrack
        {
            TrackNumber  = 1,
            SessionId    = 200,
            SessionIndex = 0,    // legacy / stale — should be IGNORED
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track, sessions);

        Assert.Equal(1, vm.Session.Value);   // resolved from SessionId == 200 → idx 1
    }

    [Fact]
    public void LoadSingle_WithSessionIdNotMatchingAnyInList_FallsBackToSessionIndex()
    {
        var sessions = TwoSessions();
        var track = new AlbumTrack
        {
            TrackNumber  = 1,
            SessionId    = 999,   // unknown — falls back to SessionIndex
            SessionIndex = 1,
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track, sessions);

        Assert.Equal(1, vm.Session.Value);
    }

    [Fact]
    public void LoadSingle_PreH21Track_WithSessionIndexOnly_StillWorks()
    {
        var sessions = TwoSessions();
        var track = new AlbumTrack
        {
            TrackNumber  = 1,
            SessionId    = null,    // pre-H21 snapshot
            SessionIndex = 1,
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track, sessions);

        Assert.Equal(1, vm.Session.Value);
    }

    [Fact]
    public void LoadSingle_WithoutSessionsList_FallsBackToSessionIndex()
    {
        // Loose-track-shape call (no sessions parameter). VM's _sessions stays
        // null; combo position comes from SessionIndex as before.
        var track = new AlbumTrack { TrackNumber = 1, SessionIndex = 0, SessionId = 999 };
        var vm = new TrackEditorViewModel();

        vm.LoadSingle(track);   // no sessions param

        Assert.Equal(0, vm.Session.Value);
    }

    // ── Save: writes BOTH SessionId and SessionIndex ─────────────────────────

    [Fact]
    public void SaveSingle_WritesBothSessionIdAndSessionIndex()
    {
        var sessions = TwoSessions();
        var disc = new AlbumDisc
        {
            DiscNumber = 1,
            Tracks = new List<AlbumTrack> { new() { TrackNumber = 1 } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(disc.Tracks[0], sessions);
        vm.Session.Value = 1;   // user picks session at position 1 (Id=200)

        var err = vm.SaveSingle(disc, 0);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, err);
        Assert.Equal(1,   disc.Tracks[0].SessionIndex);
        Assert.Equal(200, disc.Tracks[0].SessionId);
    }

    [Fact]
    public void SaveSingle_NullSession_ClearsBothFields()
    {
        var sessions = TwoSessions();
        var disc = new AlbumDisc
        {
            DiscNumber = 1,
            Tracks = new List<AlbumTrack>
            {
                new() { TrackNumber = 1, SessionId = 100, SessionIndex = 0 },
            },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(disc.Tracks[0], sessions);
        vm.Session.Value = null;   // user picks "(no session)"

        vm.SaveSingle(disc, 0);

        Assert.Null(disc.Tracks[0].SessionIndex);
        Assert.Null(disc.Tracks[0].SessionId);
    }

    [Fact]
    public void SaveSingle_SessionWithoutStableId_WritesNullSessionId()
    {
        // Freshly-added session not yet persisted (Id=0) — translation returns
        // null for SessionId so the SQLite save path falls back to the
        // positional SessionIndex.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 0, Dates = "fresh session" },
        };
        var disc = new AlbumDisc
        {
            DiscNumber = 1,
            Tracks = new List<AlbumTrack> { new() { TrackNumber = 1 } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(disc.Tracks[0], sessions);
        vm.Session.Value = 0;

        vm.SaveSingle(disc, 0);

        Assert.Equal(0, disc.Tracks[0].SessionIndex);
        Assert.Null(disc.Tracks[0].SessionId);   // can't write Id=0
    }

    [Fact]
    public void SaveLoose_ClearsBothSessionFields()
    {
        var track = new AlbumTrack
        {
            TrackNumber  = 0,
            SessionId    = 200,   // somehow set on a loose track
            SessionIndex = 1,
        };
        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);

        vm.SaveLoose(track);

        Assert.Null(track.SessionId);
        Assert.Null(track.SessionIndex);
        Assert.Equal(0, track.TrackNumber);
    }

    // ── Reorder regression ───────────────────────────────────────────────────

    [Fact]
    public void SaveSingle_AfterSessionsReorder_WritesStableSessionIdNotShiftedPosition()
    {
        // The headline H21 fix: track loaded with SessionId+SessionIndex; user
        // reorders sessions (model list order changes, IDs stay stable);
        // user clicks OK without touching the session field.
        //
        // Pre-slice-2: VM wrote only the old positional SessionIndex (now
        // pointing at the WRONG session because positions shifted), then save
        // path used the unchanged track.SessionId loaded from DB (correct).
        // Slice 2: VM writes BOTH the new positional SessionIndex AND a
        // re-resolved SessionId. The Id is unchanged through the round-trip.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100, Dates = "session A" },
            new() { Id = 200, Dates = "session B" },
        };
        var disc = new AlbumDisc
        {
            DiscNumber = 1,
            Tracks = new List<AlbumTrack>
            {
                new() { TrackNumber = 1, SessionId = 200, SessionIndex = 1 },
            },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(disc.Tracks[0], sessions);
        Assert.Equal(1, vm.Session.Value);

        // Now simulate the user reordering sessions in the UI: session B is
        // now at index 0, session A at index 1.
        sessions.Reverse();
        // Re-load to pick up the new positions. (In the real flow the editor
        // would re-render the combo; the VM tracking SessionId-by-position
        // means LoadSingle re-resolves.)
        vm.LoadSingle(disc.Tracks[0], sessions);

        Assert.Equal(0, vm.Session.Value);   // session B now at idx 0

        vm.SaveSingle(disc, 0);

        Assert.Equal(0,   disc.Tracks[0].SessionIndex);
        Assert.Equal(200, disc.Tracks[0].SessionId);   // STILL session B
    }

    // ── Multi-edit: SaveMulti writes both ─────────────────────────────────────

    [Fact]
    public void SaveMulti_PicksSession_WritesBothFieldsToEveryTrack()
    {
        var sessions = TwoSessions();
        var tracks = new List<AlbumTrack>
        {
            new() { TrackNumber = 1 },
            new() { TrackNumber = 2 },
            new() { TrackNumber = 3 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "Mixed", hasSharedSessions: true, sessions: sessions);
        vm.Session.Value = 1;   // user picks position 1 (Id=200) for all

        var err = vm.SaveMulti(tracks, allLoose: false);
        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, err);

        foreach (var t in tracks)
        {
            Assert.Equal(1,   t.SessionIndex);
            Assert.Equal(200, t.SessionId);
        }
    }

    [Fact]
    public void LoadMulti_TracksShareStableSessionIdButDifferentPositions_LoadsAsUnanimous()
    {
        // Tracks loaded from JSON snapshots taken at different times might
        // hold differing positional SessionIndex values pointing at the same
        // stable session. Loading them in multi-edit should detect the shared
        // session and load as Unanimous, not Mixed.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100, Dates = "A" },
            new() { Id = 200, Dates = "B" },
        };
        var tracks = new List<AlbumTrack>
        {
            new() { TrackNumber = 1, SessionId = 200, SessionIndex = 1 },
            // Same stable session, but a stale SessionIndex pointing somewhere
            // else (e.g. loaded from a different snapshot before reordering).
            new() { TrackNumber = 2, SessionId = 200, SessionIndex = 0 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "Mixed", hasSharedSessions: true, sessions: sessions);

        Assert.False(vm.Session.IsMixed);
        Assert.Equal(1, vm.Session.Value);   // resolved from shared Id=200
    }
}
