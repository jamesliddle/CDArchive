using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Rework H23 regression: <see cref="SessionIndexMapping"/> distinguishes
/// the three SessionBox semantics — "write this real session index",
/// "write null (no session)", and "skip the write (Mixed / multiple
/// albums sentinel)".
///
/// Pre-fix the editor collapsed <c>SessionIndex == null</c> to session 0
/// via <c>?? 0</c>, so opening a no-session track and clicking OK
/// silently wrote SessionIndex = 0. Multi-edit had the same trap when
/// every selected track shared a null session: all became session 0.
/// </summary>
public class SessionIndexMappingTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // InitialComboIndex
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 3, 0)]    // real session 0
    [InlineData(1, 3, 1)]    // real session 1
    [InlineData(2, 3, 2)]    // real session 2 (last)
    public void InitialComboIndex_RealSession_ReturnsThatIndex(
        int sessionIndex, int sessionCount, int expectedCombo)
    {
        Assert.Equal(expectedCombo, SessionIndexMapping.InitialComboIndex(sessionIndex, sessionCount));
    }

    [Theory]
    [InlineData(null, 3, 3)]    // (no session) lands at sessionCount
    [InlineData(null, 0, 0)]    // empty session list — "(no session)" at index 0
    public void InitialComboIndex_NullSessionIndex_LandsOnNoSessionPseudoItem(
        int? sessionIndex, int sessionCount, int expectedCombo)
    {
        Assert.Equal(expectedCombo, SessionIndexMapping.InitialComboIndex(sessionIndex, sessionCount));
    }

    [Theory]
    [InlineData(-1, 3, 3)]   // negative session index — fall back to (no session)
    [InlineData(99, 3, 3)]   // out-of-range — fall back to (no session)
    [InlineData(3, 3, 3)]    // exactly at sessionCount (out of range) — fall back
    public void InitialComboIndex_OutOfRangeSession_FallsBackToNoSession(
        int sessionIndex, int sessionCount, int expectedCombo)
    {
        Assert.Equal(expectedCombo,
            SessionIndexMapping.InitialComboIndex(sessionIndex, sessionCount));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveSelection — single-edit (no Mixed sentinel, pass -1)
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 3, 0)]    // real session 0
    [InlineData(1, 3, 1)]
    [InlineData(2, 3, 2)]    // real session 2 (last)
    public void ResolveSelection_SingleEdit_RealSession_ReturnsSessionIndex(
        int comboIdx, int sessionCount, int expected)
    {
        var result = SessionIndexMapping.ResolveSelection(
            comboIdx, sessionCount, mixedSentinelIndex: -1, out var isMixed);
        Assert.Equal(expected, result);
        Assert.False(isMixed);
    }

    [Theory]
    [InlineData(3, 3)]   // "(no session)" pseudo-item position
    [InlineData(-1, 3)]  // no selection at all
    public void ResolveSelection_SingleEdit_NoSession_ReturnsNullWithoutSkip(
        int comboIdx, int sessionCount)
    {
        // The fix: "(no session)" is a deliberate "write null" — the caller
        // SHOULD write, just with null. isMixedSentinel must be false so
        // ApplyUiToTrack doesn't accidentally treat it as "skip".
        var result = SessionIndexMapping.ResolveSelection(
            comboIdx, sessionCount, mixedSentinelIndex: -1, out var isMixed);
        Assert.Null(result);
        Assert.False(isMixed);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ResolveSelection — multi-edit (Mixed sentinel after "(no session)")
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveSelection_MultiEdit_MixedSentinel_IsSkipWriteSignal()
    {
        // 3 real sessions → indices 0..2. "(no session)" at 3. "Mixed" at 4.
        var result = SessionIndexMapping.ResolveSelection(
            selectedComboIndex: 4,
            sessionCount: 3,
            mixedSentinelIndex: 4,
            out var isMixed);
        Assert.True(isMixed);
        // The returned value is null but the caller must skip the write.
        Assert.Null(result);
    }

    [Fact]
    public void ResolveSelection_MultiEdit_NoSessionItem_WritesNullNotSkip()
    {
        // The H23 regression itself: when every selected track has
        // SessionIndex==null, the combo lands on "(no session)" at index 3.
        // SaveMulti must write null (not session 0, and not skip).
        var result = SessionIndexMapping.ResolveSelection(
            selectedComboIndex: 3,
            sessionCount: 3,
            mixedSentinelIndex: 4,
            out var isMixed);
        Assert.Null(result);
        Assert.False(isMixed);
    }

    [Fact]
    public void ResolveSelection_MultiEdit_RealSession_ReturnsThatIndex()
    {
        var result = SessionIndexMapping.ResolveSelection(
            selectedComboIndex: 1,
            sessionCount: 3,
            mixedSentinelIndex: 4,
            out var isMixed);
        Assert.Equal(1, result);
        Assert.False(isMixed);
    }

    [Fact]
    public void ResolveSelection_MultiAlbumDisabledCombo_TreatedAsMixedSentinel()
    {
        // "(multiple albums — cannot edit)" case: combo has one item at index 0,
        // sessionCount is 0. The single item IS the skip-write sentinel.
        var result = SessionIndexMapping.ResolveSelection(
            selectedComboIndex: 0,
            sessionCount: 0,
            mixedSentinelIndex: 0,
            out var isMixed);
        Assert.True(isMixed);
        Assert.Null(result);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Sentinel-position helpers
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(99, 99)]
    public void NoSessionItemIndex_IsAlwaysSessionCount(int sessionCount, int expected)
    {
        Assert.Equal(expected, SessionIndexMapping.NoSessionItemIndex(sessionCount));
    }

    [Theory]
    [InlineData(3, true, 4)]    // 3 real + (no session) at 3 + Mixed at 4
    [InlineData(0, true, 1)]    // 0 real + (no session) at 0 + Mixed at 1
    [InlineData(3, false, -1)]  // no Mixed sentinel present
    public void MixedSentinelIndex_IsOneAfterNoSession_WhenPresent(
        int sessionCount, bool present, int expected)
    {
        Assert.Equal(expected, SessionIndexMapping.MixedSentinelIndex(sessionCount, present));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RemapTracksAfterSessionRemoval — H21 slice 1 (positional fallback)
    // ─────────────────────────────────────────────────────────────────────────
    //
    // These tests exercise the positional fallback path (track.SessionId is
    // null). The session list is a placeholder of the right size — every
    // session has Id=0 so the helper routes through the positional branch.

    private static List<RecordingSession> PositionalSessions(int count) =>
        Enumerable.Range(0, count).Select(_ => new RecordingSession { Id = 0 }).ToList();

    [Fact]
    public void Remap_TrackPointingAtRemovedSession_BecomesNull()
    {
        var sessions = PositionalSessions(2);
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SessionIndex = 1 },  // → removed
            new AlbumTrack { TrackNumber = 2, SessionIndex = 1 },  // → removed
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(2, changed);
        Assert.Null(tracks[0].SessionIndex);
        Assert.Null(tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_TrackPointingAtLaterSession_DecrementsByOne()
    {
        // Sessions [0, 1, 2, 3]; removing index 1. Tracks at indices 2, 3
        // should move to 1, 2 (same logical sessions, new positions).
        var sessions = PositionalSessions(4);
        var tracks = new[]
        {
            new AlbumTrack { SessionIndex = 2 },  // → 1
            new AlbumTrack { SessionIndex = 3 },  // → 2
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(2, changed);
        Assert.Equal(1, tracks[0].SessionIndex);
        Assert.Equal(2, tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_TrackPointingAtEarlierSession_Unchanged()
    {
        var sessions = PositionalSessions(3);
        var tracks = new[]
        {
            new AlbumTrack { SessionIndex = 0 },  // stays 0 — earlier than removed (1)
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(0, changed);
        Assert.Equal(0, tracks[0].SessionIndex);
    }

    [Fact]
    public void Remap_TrackWithNullSession_Unchanged()
    {
        var sessions = PositionalSessions(1);
        var tracks = new[]
        {
            new AlbumTrack { SessionIndex = null },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(0, sessions, tracks);

        Assert.Equal(0, changed);
        Assert.Null(tracks[0].SessionIndex);
    }

    [Fact]
    public void Remap_MixedTracks_AllCasesAtOnce()
    {
        // Sessions [0, 1, 2]; remove index 1.
        // - SessionIndex=0 → unchanged (earlier than removed)
        // - SessionIndex=1 → null (removed session itself)
        // - SessionIndex=2 → 1 (later than removed)
        // - SessionIndex=null → unchanged (no session)
        var sessions = PositionalSessions(3);
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SessionIndex = 0 },
            new AlbumTrack { TrackNumber = 2, SessionIndex = 1 },
            new AlbumTrack { TrackNumber = 3, SessionIndex = 2 },
            new AlbumTrack { TrackNumber = 4, SessionIndex = null },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(2, changed);  // tracks 2 and 3 changed
        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Null(tracks[1].SessionIndex);
        Assert.Equal(1, tracks[2].SessionIndex);
        Assert.Null(tracks[3].SessionIndex);
    }

    [Fact]
    public void Remap_NegativeRemovedIndex_NoOp()
    {
        // Defensive: caller passed a "session not found" signal (-1).
        var sessions = PositionalSessions(2);
        var tracks = new[]
        {
            new AlbumTrack { SessionIndex = 0 },
            new AlbumTrack { SessionIndex = 1 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(-1, sessions, tracks);

        Assert.Equal(0, changed);
        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Equal(1, tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_OutOfRangeRemovedIndex_NoOp()
    {
        var sessions = PositionalSessions(2);
        var tracks = new[]
        {
            new AlbumTrack { SessionIndex = 0 },
            new AlbumTrack { SessionIndex = 1 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(5, sessions, tracks);

        Assert.Equal(0, changed);
        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Equal(1, tracks[1].SessionIndex);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RemapTracksAfterSessionRemoval — H21 slice 3 (stable-Id path)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Remap_StableId_TrackPointingAtRemovedSession_ClearsBothFields()
    {
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },
            new() { Id = 200 },   // ← removed
            new() { Id = 300 },
        };
        var tracks = new[]
        {
            new AlbumTrack { SessionId = 200, SessionIndex = 1 },
            new AlbumTrack { SessionId = 200, SessionIndex = 1 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(2, changed);
        foreach (var t in tracks)
        {
            Assert.Null(t.SessionId);
            Assert.Null(t.SessionIndex);
        }
    }

    [Fact]
    public void Remap_StableId_SurvivingSession_KeepsSessionIdAndUpdatesIndex()
    {
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },   // ← removed
            new() { Id = 200 },
            new() { Id = 300 },
        };
        var tracks = new[]
        {
            // Pre: position 1 → session Id=200.
            new AlbumTrack { SessionId = 200, SessionIndex = 1 },
            // Pre: position 2 → session Id=300.
            new AlbumTrack { SessionId = 300, SessionIndex = 2 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(0, sessions, tracks);

        Assert.Equal(2, changed);
        // Both tracks keep their stable Id; SessionIndex shifts down by 1.
        Assert.Equal(200, tracks[0].SessionId);
        Assert.Equal(0,   tracks[0].SessionIndex);
        Assert.Equal(300, tracks[1].SessionId);
        Assert.Equal(1,   tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_StableId_OrphanSessionId_ClearsBothFields()
    {
        // Track references SessionId=999 which doesn't appear in the sessions
        // list (data corruption / stale ref). The helper clears both fields.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },
            new() { Id = 200 },
        };
        var tracks = new[]
        {
            new AlbumTrack { SessionId = 999, SessionIndex = 0 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(0, sessions, tracks);

        Assert.Equal(1, changed);
        Assert.Null(tracks[0].SessionId);
        Assert.Null(tracks[0].SessionIndex);
    }

    [Fact]
    public void Remap_StableId_NoChangeNeeded_DoesNotMutate()
    {
        // Removing the LAST session; the surviving session is at position 0
        // both before and after. A track pointing at it should not be marked
        // as changed.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },
            new() { Id = 200 },   // ← removed
        };
        var tracks = new[]
        {
            new AlbumTrack { SessionId = 100, SessionIndex = 0 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(0, changed);
        Assert.Equal(100, tracks[0].SessionId);
        Assert.Equal(0,   tracks[0].SessionIndex);
    }

    [Fact]
    public void Remap_StableId_AfterListReorder_PointersFollowTheLogicalSession()
    {
        // The headline H21 motivator: tracks anchored on SessionId survive
        // arbitrary reorders of the album's sessions list. Here we reorder
        // BEFORE invoking the helper (simulating editing flow); the helper
        // still re-anchors correctly to the right surviving sessions.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 300 },   // logical "C" — moved to position 0
            new() { Id = 100 },   // logical "A" — moved to position 1
            new() { Id = 200 },   // logical "B" — ← removed
        };
        var tracks = new[]
        {
            // Track originally pointed at logical "A" (whatever position).
            new AlbumTrack { SessionId = 100, SessionIndex = 1 },
            // Track originally pointed at logical "C".
            new AlbumTrack { SessionId = 300, SessionIndex = 0 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(2, sessions, tracks);

        // Track[0] was SessionIndex=1, pointing at A (id=100). After removal
        // A is still at position 1 (sessions[1]). No change.
        // Track[1] was SessionIndex=0, pointing at C (id=300). After removal
        // C is still at position 0 (sessions[0]). No change.
        Assert.Equal(0, changed);
        Assert.Equal(100, tracks[0].SessionId);
        Assert.Equal(1,   tracks[0].SessionIndex);
        Assert.Equal(300, tracks[1].SessionId);
        Assert.Equal(0,   tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_StableId_TrackWithoutSessionId_FallsBackToPositional()
    {
        // Mixed batch: one pre-H21 track (SessionId null), one slice-2+ track.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },   // ← removed
            new() { Id = 200 },
        };
        var tracks = new[]
        {
            // Pre-H21 track: SessionId null, SessionIndex 1. Positional path:
            // removed=0, si=1 → decrement to 0.
            new AlbumTrack { SessionId = null, SessionIndex = 1 },
            // Slice-2 track: stable-Id path; survives at new position 0.
            new AlbumTrack { SessionId = 200, SessionIndex = 1 },
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(0, sessions, tracks);

        Assert.Equal(2, changed);
        Assert.Null(tracks[0].SessionId);
        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Equal(200, tracks[1].SessionId);
        Assert.Equal(0, tracks[1].SessionIndex);
    }

    [Fact]
    public void Remap_StableId_FreshlyAddedSessionRemoved_PositionalPathRunsForOthers()
    {
        // Edge case: removed session has Id=0 (freshly added, never saved).
        // Stable-Id path can't match anything against removedId=0 (since
        // tracks won't have SessionId=0). Positional fallback handles
        // affected tracks.
        var sessions = new List<RecordingSession>
        {
            new() { Id = 100 },
            new() { Id = 0   },   // ← fresh, removed
            new() { Id = 300 },
        };
        var tracks = new[]
        {
            new AlbumTrack { SessionId = 300, SessionIndex = 2 },   // survives, shifts to 1
        };

        var changed = SessionIndexMapping.RemapTracksAfterSessionRemoval(1, sessions, tracks);

        Assert.Equal(1, changed);
        Assert.Equal(300, tracks[0].SessionId);
        Assert.Equal(1,   tracks[0].SessionIndex);
    }
}
