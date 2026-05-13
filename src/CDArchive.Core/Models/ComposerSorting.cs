namespace CDArchive.Core.Models;

/// <summary>
/// Sort modes for the composer list in the Canon view.
/// </summary>
public enum ComposerSortField
{
    /// <summary>Number of pieces by this composer (default — descending).</summary>
    Pieces,
    Name,
    /// <summary>Year of birth.</summary>
    Birth,
    /// <summary>Year of death.</summary>
    Death,
    /// <summary>Number of distinct albums referencing this composer's works (descending).</summary>
    Recordings,
}

/// <summary>
/// Centralised composer-sort logic. UI-free and unit-testable; the runtime
/// caller passes a recording-count delegate over
/// <c>PieceReferenceIndex.CountForComposer</c>, while tests pass a stub.
/// </summary>
public static class ComposerSorting
{
    /// <summary>
    /// Orders <paramref name="composers"/> by <paramref name="field"/> in the
    /// requested direction. Each ordering tie-breaks by <see cref="CanonComposer.SortName"/>
    /// so the output is deterministic across input orderings.
    /// </summary>
    public static List<CanonComposer> Sort(
        IEnumerable<CanonComposer> composers,
        ComposerSortField field,
        bool ascending,
        Func<CanonComposer, int>? recordingCount = null)
    {
        // Pre-materialise so the sort can read each input once.
        var list = composers.ToList();
        return field switch
        {
            ComposerSortField.Pieces     => OrderByPieces(list, ascending),
            ComposerSortField.Birth      => OrderByYear(list, ascending, c => c.BirthYearSort),
            ComposerSortField.Death      => OrderByYear(list, ascending, c => c.DeathYearSort),
            ComposerSortField.Recordings => OrderByRecordings(list, ascending, recordingCount),
            ComposerSortField.Name       => OrderByName(list, ascending),
            _                            => OrderByName(list, true),
        };
    }

    /// <summary>
    /// Parses a UI-side label and returns (field, defaultDirection). The
    /// direction defaults match the existing UX:
    /// <list type="bullet">
    ///   <item>Pieces / Recordings — most first (descending).</item>
    ///   <item>Name — A → Z (ascending).</item>
    ///   <item>Born / Died — oldest first (ascending).</item>
    /// </list>
    /// Unknown labels fall through to (Pieces, descending) — the
    /// XAML <c>SelectedIndex="0"</c> default for the Sort Composers combo.
    /// </summary>
    public static (ComposerSortField field, bool ascending) ParseField(string? raw) => raw switch
    {
        "Pieces"     => (ComposerSortField.Pieces,     false),
        "Name"       => (ComposerSortField.Name,       true),
        "Born"       => (ComposerSortField.Birth,      true),
        "Died"       => (ComposerSortField.Death,      true),
        "Recordings" => (ComposerSortField.Recordings, false),
        _            => (ComposerSortField.Pieces,     false),
    };

    // ── Sort impls ──────────────────────────────────────────────────────────

    private static List<CanonComposer> OrderByPieces(List<CanonComposer> list, bool asc) =>
        (asc
            ? list.OrderBy(c => c.PieceCount)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase)
            : list.OrderByDescending(c => c.PieceCount)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase))
        .ToList();

    private static List<CanonComposer> OrderByName(List<CanonComposer> list, bool asc)
    {
        // Fall back to Name when SortName is missing — the existing XAML
        // sort key behaviour. SortName is usually populated for canonical
        // entries; the fallback covers ad-hoc "(Various)"-style sentinels.
        static string Key(CanonComposer c) => !string.IsNullOrEmpty(c.SortName) ? c.SortName : c.Name;
        return (asc
            ? list.OrderBy(Key, StringComparer.OrdinalIgnoreCase)
            : list.OrderByDescending(Key, StringComparer.OrdinalIgnoreCase))
        .ToList();
    }

    private static List<CanonComposer> OrderByYear(List<CanonComposer> list, bool asc, Func<CanonComposer, int> key) =>
        (asc
            ? list.OrderBy(key)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase)
            : list.OrderByDescending(key)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase))
        .ToList();

    private static List<CanonComposer> OrderByRecordings(
        List<CanonComposer> list, bool asc, Func<CanonComposer, int>? recordingCount)
    {
        var count = recordingCount ?? (_ => 0);
        return (asc
            ? list.OrderBy(count)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase)
            : list.OrderByDescending(count)
                  .ThenBy(c => c.SortName, StringComparer.OrdinalIgnoreCase))
        .ToList();
    }
}
