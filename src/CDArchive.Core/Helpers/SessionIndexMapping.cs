using CDArchive.Core.Models;

namespace CDArchive.Core.Helpers;

/// <summary>
/// Pure logic for mapping between an <c>AlbumTrack.SessionIndex</c> (an
/// <c>int?</c> position into the album's session list) and a
/// <c>ComboBox.SelectedIndex</c> with two sentinels:
///
/// <list type="bullet">
///   <item>A "(no session)" pseudo-item appended after the real sessions.
///     Selecting it on save writes <c>SessionIndex = null</c>. Pre-fix
///     (Rework H23) the editor collapsed null to session 0 via
///     <c>?? 0</c>, so a track with no session that was simply opened
///     and OK'd silently became session 0 — invisible data corruption.</item>
///   <item>In multi-edit mode only, a "Mixed" sentinel appended after
///     "(no session)" when the selected tracks have differing session
///     values. Skip the write when this is still selected.</item>
/// </list>
///
/// <para>
/// Extracted from <c>TrackEditorWindow.xaml.cs</c> so the mapping can be
/// unit-tested without a WPF host. The window stores the resolved
/// item indices in private fields and forwards selection events through
/// this helper.
/// </para>
/// </summary>
public static class SessionIndexMapping
{
    /// <summary>
    /// Sentinel value used in <see cref="ResolveSelection"/> to indicate
    /// "the user is still on the Mixed sentinel — skip the write".
    /// Distinguishes the "wrote null deliberately" case from the
    /// "don't touch this field" case in multi-edit.
    /// </summary>
    public static int? MixedSentinel { get; } = null;

    /// <summary>
    /// Returns the position of the "(no session)" pseudo-item after
    /// <paramref name="sessionCount"/> real session items. Always equal
    /// to <paramref name="sessionCount"/>.
    /// </summary>
    public static int NoSessionItemIndex(int sessionCount) => sessionCount;

    /// <summary>
    /// Returns the position of the "Mixed" sentinel — only present in
    /// multi-edit mode when tracks differ. -1 means no Mixed sentinel
    /// is present.
    /// </summary>
    public static int MixedSentinelIndex(int sessionCount, bool mixedSentinelPresent)
        => mixedSentinelPresent ? sessionCount + 1 : -1;

    /// <summary>
    /// Computes the initial <c>ComboBox.SelectedIndex</c> for a
    /// SessionBox seeded from <paramref name="selectedSessionIndex"/>.
    /// Null (no session) maps to the "(no session)" pseudo-item; a valid
    /// session index maps to itself; any out-of-range value falls back
    /// to "(no session)" rather than the pre-fix silent "session 0"
    /// collapse.
    /// </summary>
    public static int InitialComboIndex(int? selectedSessionIndex, int sessionCount)
    {
        if (selectedSessionIndex is { } i && i >= 0 && i < sessionCount)
            return i;
        return NoSessionItemIndex(sessionCount);
    }

    /// <summary>
    /// Resolves a SessionBox <c>SelectedIndex</c> back to the
    /// <c>AlbumTrack.SessionIndex</c> value to write on save.
    /// </summary>
    /// <param name="selectedComboIndex">The combo's current SelectedIndex.</param>
    /// <param name="sessionCount">Count of real sessions in the album.</param>
    /// <param name="mixedSentinelIndex">
    /// The combo position of the "Mixed" / "(multiple albums)" sentinel,
    /// or <c>-1</c> when no skip-write sentinel is present. In multi-edit
    /// with mixed values this is typically <c>sessionCount + 1</c> (just
    /// after the "(no session)" pseudo-item); in the "(multiple albums)"
    /// disabled-combo case it's <c>0</c>.
    /// </param>
    /// <param name="isMixedSentinel">
    /// Output: true when the resolved selection is the Mixed sentinel —
    /// the caller should SKIP writing rather than writing the returned
    /// (null) value, since the sentinel means "don't touch this field on
    /// the selected tracks".
    /// </param>
    /// <returns>
    /// The session index to write (<see cref="AlbumTrack.SessionIndex"/>):
    /// <list type="bullet">
    ///   <item>0..sessionCount-1 — the selected real session.</item>
    ///   <item>null — the "(no session)" pseudo-item is selected (or the
    ///     selection is out of range), and the caller should write null.</item>
    /// </list>
    /// When <paramref name="isMixedSentinel"/> is true the returned value
    /// is null but the caller should skip the write entirely.
    /// </returns>
    public static int? ResolveSelection(
        int selectedComboIndex,
        int sessionCount,
        int mixedSentinelIndex,
        out bool isMixedSentinel)
    {
        isMixedSentinel = mixedSentinelIndex >= 0 && selectedComboIndex == mixedSentinelIndex;
        if (isMixedSentinel) return null;
        if (selectedComboIndex < 0 || selectedComboIndex >= sessionCount) return null;
        return selectedComboIndex;
    }

    /// <summary>
    /// Defensive translation for when a session is removed from an album's
    /// <see cref="CanonAlbum.Sessions"/> list — every track's positional
    /// <see cref="AlbumTrack.SessionIndex"/> needs to be re-anchored to point
    /// at the same logical session it pointed at before the removal.
    ///
    /// <para>This is the "first slice" of H21: SessionIndex is still a
    /// positional reference (not a stable Id), but the editor can no longer
    /// silently mis-point existing tracks when the user removes a session.
    /// The full fix (stable Id on RecordingSession + a one-shot migration)
    /// is the larger remaining H21 work.</para>
    ///
    /// <list type="bullet">
    ///   <item>A track pointing AT the removed session — its referenced
    ///     session no longer exists. The most conservative outcome is
    ///     <c>null</c> ("no session"), which the user can then re-assign.</item>
    ///   <item>A track pointing at a session AFTER the removed one
    ///     (higher index) — its index must decrement by 1 so the link
    ///     continues to address the same logical session.</item>
    ///   <item>A track pointing at a session BEFORE the removed one
    ///     (lower index) — unaffected, the underlying session keeps its
    ///     position.</item>
    /// </list>
    ///
    /// <para>The walk is destructive: each track's <c>SessionIndex</c> is
    /// mutated in place. Call BEFORE removing the session from the list,
    /// so the index parameter is still meaningful relative to the input.</para>
    /// </summary>
    /// <param name="removedSessionIndex">The position of the session about
    /// to be removed in the album's session list.</param>
    /// <param name="tracks">Every track on every disc of the album — the
    /// caller should flatten <c>album.Discs.SelectMany(d => d.Tracks)</c>.</param>
    /// <returns>
    /// The number of tracks whose <c>SessionIndex</c> was actually changed
    /// — used by the editor for status messages.
    /// </returns>
    public static int RemapTracksAfterSessionRemoval(
        int removedSessionIndex,
        IEnumerable<AlbumTrack> tracks)
    {
        if (removedSessionIndex < 0) return 0;
        int changed = 0;
        foreach (var t in tracks)
        {
            if (t.SessionIndex is not int si) continue;
            if (si == removedSessionIndex)
            {
                t.SessionIndex = null;
                changed++;
            }
            else if (si > removedSessionIndex)
            {
                t.SessionIndex = si - 1;
                changed++;
            }
            // si < removedSessionIndex: unaffected, the lower-positioned
            // session keeps its slot.
        }
        return changed;
    }
}
