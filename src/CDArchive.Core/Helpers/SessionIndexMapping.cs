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
}
