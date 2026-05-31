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
    /// <see cref="CanonAlbum.Sessions"/> list — every track's session
    /// reference needs to be re-anchored to point at the same logical session
    /// it pointed at before the removal.
    ///
    /// <para>H21 slice 3: stable-Id-aware. Tracks that carry
    /// <see cref="AlbumTrack.SessionId"/> are re-anchored by Id (resilient to
    /// list reorders); tracks without a stable Id (pre-H21 snapshots or
    /// freshly-added sessions that haven't been persisted) fall back to the
    /// pre-H21 positional walk.</para>
    ///
    /// <list type="bullet">
    ///   <item><b>Stable-Id path</b> (track.SessionId is set and non-zero):
    ///     <list type="bullet">
    ///       <item>SessionId matches the removed session's Id → both fields
    ///         clear to null ("no session").</item>
    ///       <item>SessionId matches a surviving session → SessionIndex
    ///         updates to the survivor's new position; SessionId stays
    ///         pointed at the same logical session.</item>
    ///       <item>SessionId matches no current session (orphan ref) → both
    ///         fields clear to null.</item>
    ///     </list>
    ///   </item>
    ///   <item><b>Positional fallback</b> (track.SessionId is null/0):
    ///     <list type="bullet">
    ///       <item>SessionIndex == removedIndex → null.</item>
    ///       <item>SessionIndex &gt; removedIndex → decrement by 1.</item>
    ///       <item>SessionIndex &lt; removedIndex → unchanged.</item>
    ///     </list>
    ///   </item>
    /// </list>
    ///
    /// <para>The walk is destructive: each track's session fields are
    /// mutated in place. Call BEFORE removing the session from the list, so
    /// <paramref name="removedSessionIndex"/> still indexes into
    /// <paramref name="sessionsBeforeRemoval"/>.</para>
    /// </summary>
    /// <param name="removedSessionIndex">The position of the session about
    /// to be removed within <paramref name="sessionsBeforeRemoval"/>.</param>
    /// <param name="sessionsBeforeRemoval">The album's session list at the
    /// moment of the call — i.e. still containing the session about to be
    /// removed. Used to look up the removed session's stable Id and to
    /// compute the post-removal positions of surviving sessions.</param>
    /// <param name="tracks">Every track on every disc of the album — the
    /// caller should flatten <c>album.Discs.SelectMany(d => d.Tracks)</c>.</param>
    /// <returns>
    /// The number of tracks whose session reference was actually changed
    /// — used by the editor for status messages.
    /// </returns>
    public static int RemapTracksAfterSessionRemoval(
        int removedSessionIndex,
        IReadOnlyList<RecordingSession> sessionsBeforeRemoval,
        IEnumerable<AlbumTrack> tracks)
    {
        if (removedSessionIndex < 0 || removedSessionIndex >= sessionsBeforeRemoval.Count)
            return 0;

        var removedSession = sessionsBeforeRemoval[removedSessionIndex];
        var removedId = removedSession.Id;   // 0 if not persisted

        // Build the post-removal position map for surviving sessions, keyed
        // by stable Id. Sessions with Id=0 (freshly-added, not yet saved) are
        // skipped: tracks pointing at them have no stable handle so they
        // route through the positional fallback below.
        var newPositionById = new Dictionary<long, int>();
        for (int i = 0; i < sessionsBeforeRemoval.Count; i++)
        {
            if (i == removedSessionIndex) continue;
            var s = sessionsBeforeRemoval[i];
            if (s.Id == 0) continue;
            var newPos = i < removedSessionIndex ? i : i - 1;
            newPositionById[s.Id] = newPos;
        }

        int changed = 0;
        foreach (var t in tracks)
        {
            // Stable-Id path: prefer the SessionId reference where present.
            if (t.SessionId is long sid && sid != 0)
            {
                if (sid == removedId)
                {
                    t.SessionId    = null;
                    t.SessionIndex = null;
                    changed++;
                }
                else if (newPositionById.TryGetValue(sid, out var newPos))
                {
                    // The session survives; update SessionIndex to its new
                    // position. SessionId is already correct (unchanged).
                    if (t.SessionIndex != newPos)
                    {
                        t.SessionIndex = newPos;
                        changed++;
                    }
                }
                else
                {
                    // Orphan SessionId not in the current list — clear both.
                    t.SessionId    = null;
                    t.SessionIndex = null;
                    changed++;
                }
                continue;
            }

            // Positional fallback: pre-H21 tracks, or tracks that reference a
            // freshly-added session (Id=0). Same logic as the pre-slice-3
            // implementation, retained for back-compat.
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
        }
        return changed;
    }

    /// <summary>
    /// Updates every track's positional <see cref="AlbumTrack.SessionIndex"/>
    /// after a session has been moved (e.g. via Up/Down buttons).
    ///
    /// <para>Two paths, mirroring <see cref="RemapTracksAfterSessionRemoval"/>:</para>
    /// <list type="bullet">
    ///   <item><b>Stable-Id (post-H21)</b> — tracks carrying a
    ///     <see cref="AlbumTrack.SessionId"/> have their <see cref="AlbumTrack.SessionIndex"/>
    ///     overwritten with the session's new position in
    ///     <paramref name="sessionsAfterMove"/>. SessionId itself stays correct
    ///     across reorders (that's the whole point of the stable Id).</item>
    ///   <item><b>Positional fallback</b> — tracks with only a
    ///     <see cref="AlbumTrack.SessionIndex"/> get standard
    ///     list-move-pull-through arithmetic. Safe for any
    ///     <paramref name="fromIndex"/> / <paramref name="toIndex"/> pair, not
    ///     just adjacent swaps — but the editor's Up/Down buttons always pass
    ///     adjacent indices today.</item>
    /// </list>
    ///
    /// <para>Call AFTER the move (the helper inspects the new list to discover
    /// each session's current position). Differs from
    /// <see cref="RemapTracksAfterSessionRemoval"/> which is called BEFORE the
    /// removal — moves are 1-step swaps where the post-move state is the
    /// natural argument; removals need to look up the removed session's Id
    /// while it's still in the list.</para>
    /// </summary>
    /// <param name="fromIndex">The session's original position.</param>
    /// <param name="toIndex">The session's new position.</param>
    /// <param name="sessionsAfterMove">The album's session list after the move
    /// has been applied — i.e. the session that was at <paramref name="fromIndex"/>
    /// is now at <paramref name="toIndex"/>.</param>
    /// <param name="tracks">Every track on every disc of the album.</param>
    /// <returns>The number of tracks whose <see cref="AlbumTrack.SessionIndex"/>
    /// was actually changed.</returns>
    public static int RemapTracksAfterSessionMove(
        int fromIndex,
        int toIndex,
        IReadOnlyList<RecordingSession> sessionsAfterMove,
        IEnumerable<AlbumTrack> tracks)
    {
        if (fromIndex == toIndex) return 0;
        if (fromIndex < 0 || toIndex < 0) return 0;
        if (fromIndex >= sessionsAfterMove.Count || toIndex >= sessionsAfterMove.Count) return 0;

        // Build stable-id → current-position map for post-H21 SessionId lookups.
        var posById = new Dictionary<long, int>();
        for (int i = 0; i < sessionsAfterMove.Count; i++)
        {
            var s = sessionsAfterMove[i];
            if (s.Id != 0) posById[s.Id] = i;
        }

        int changed = 0;
        foreach (var t in tracks)
        {
            int? targetIndex = null;

            if (t.SessionId is long sid && sid != 0 && posById.TryGetValue(sid, out var newPos))
            {
                // Stable-Id path: SessionId already correct; sync SessionIndex.
                targetIndex = newPos;
            }
            else if (t.SessionIndex is int si)
            {
                // Positional fallback: list-move-pull-through arithmetic.
                if (si == fromIndex)
                    targetIndex = toIndex;
                else if (fromIndex < toIndex && si > fromIndex && si <= toIndex)
                    targetIndex = si - 1;
                else if (fromIndex > toIndex && si < fromIndex && si >= toIndex)
                    targetIndex = si + 1;
                else
                    targetIndex = si;   // unaffected
            }

            if (targetIndex.HasValue && t.SessionIndex != targetIndex.Value)
            {
                t.SessionIndex = targetIndex.Value;
                changed++;
            }
        }
        return changed;
    }
}
