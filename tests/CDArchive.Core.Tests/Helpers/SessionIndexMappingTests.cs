using CDArchive.Core.Helpers;

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
}
