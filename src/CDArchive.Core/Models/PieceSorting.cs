namespace CDArchive.Core.Models;

/// <summary>
/// Sort modes available for the piece list under each composer in the
/// Canon view. Catalogue is the default — most browsing of classical music
/// is by opus / catalogue number.
/// </summary>
public enum PieceSortField
{
    Catalogue,
    Title,
    Category,
    Year,
    /// <summary>Number of distinct albums referencing the piece (descending).</summary>
    Recordings,
}

/// <summary>
/// Centralised piece-sort logic. Owned <see cref="CanonPiece"/> entries and
/// <see cref="CrossComposerSubpieceNode"/> entries (e.g. Ravel's Fanfare from
/// L'éventail de Jeanne, surfaced under each contributing composer) are
/// merged into a single list and ordered by the selected field. Cross-composer
/// nodes inherit their parent piece's catalogue / category / year, so they
/// interleave naturally with owned pieces.
/// <para>
/// Lives in Core (no UI dependencies) so the sort behaviour is unit-testable
/// without spinning up WPF.
/// </para>
/// </summary>
public static class PieceSorting
{
    /// <summary>
    /// Returns a merged, ordered list of pieces and cross-composer nodes.
    /// <para>
    /// <paramref name="recordingCount"/> is required only for
    /// <see cref="PieceSortField.Recordings"/>; for other fields it is
    /// ignored and may be null. The function receives either a
    /// <see cref="CanonPiece"/> or a <see cref="CrossComposerSubpieceNode"/>
    /// and returns the album-hit count for that item — at runtime the caller
    /// passes a delegate over <c>PieceReferenceIndex.CountForPiece</c>; tests
    /// pass a stub.
    /// </para>
    /// </summary>
    public static List<object> Sort(
        IEnumerable<CanonPiece> ownedPieces,
        IEnumerable<CrossComposerSubpieceNode>? crossComposerNodes,
        PieceSortField field,
        Func<object, int>? recordingCount = null)
    {
        var items = new List<object>();
        foreach (var p in ownedPieces) items.Add(p);
        if (crossComposerNodes is not null)
            foreach (var n in crossComposerNodes) items.Add(n);

        return field switch
        {
            PieceSortField.Title      => OrderByTitle(items),
            PieceSortField.Category   => OrderByCategory(items),
            PieceSortField.Year       => OrderByYear(items),
            PieceSortField.Recordings => OrderByRecordings(items, recordingCount),
            _                         => OrderByCatalogue(items),
        };
    }

    /// <summary>
    /// Parses a free-form sort field string (the values bound to the combo
    /// box ContentItems in CanonView.xaml) into the enum. Unknown values
    /// fall back to Catalogue — matches the XAML <c>SelectedIndex="0"</c>
    /// default, which is the "Catalogue" entry.
    /// </summary>
    public static PieceSortField ParseField(string? raw) => raw switch
    {
        "Title"      => PieceSortField.Title,
        "Category"   => PieceSortField.Category,
        "Year"       => PieceSortField.Year,
        "Recordings" => PieceSortField.Recordings,
        _            => PieceSortField.Catalogue,
    };

    // ── Sort implementations ────────────────────────────────────────────────

    private static List<object> OrderByCatalogue(List<object> items) =>
        items.OrderBy(o => CatPrefix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => CatNumber(o))
             .ThenBy(o => CatSuffix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => Title(o),     StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static List<object> OrderByTitle(List<object> items) =>
        items.OrderBy(o => Title(o), StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static List<object> OrderByCategory(List<object> items) =>
        items.OrderBy(o => Category(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => CatPrefix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => CatNumber(o))
             .ThenBy(o => CatSuffix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => Title(o),     StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static List<object> OrderByYear(List<object> items) =>
        items.OrderBy(o => Year(o))
             .ThenBy(o => CatPrefix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => CatNumber(o))
             .ThenBy(o => CatSuffix(o), StringComparer.OrdinalIgnoreCase)
             .ThenBy(o => Title(o),     StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>
    /// Most-recorded first; ties broken by catalogue then title so the
    /// result is deterministic. When <paramref name="recordingCount"/> is
    /// null (no PieceReferenceIndex available — e.g. a unit test that
    /// hasn't built one) every item gets count 0 and the result collapses
    /// to a catalogue-tie-break order.
    /// </summary>
    private static List<object> OrderByRecordings(List<object> items, Func<object, int>? recordingCount)
    {
        var count = recordingCount ?? (_ => 0);
        return items.OrderByDescending(o => count(o))
                    .ThenBy(o => CatPrefix(o), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => CatNumber(o))
                    .ThenBy(o => CatSuffix(o), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(o => Title(o),     StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }

    // ── Field accessors that handle both shapes ─────────────────────────────
    // CanonPiece exposes these directly. CrossComposerSubpieceNode
    // delegates to its TopPiece for catalogue / year / category — so a
    // cross-credit movement sorts as if its parent piece were here.

    private static string CatPrefix(object o) => o switch
    {
        CanonPiece p                  => p.CatalogSortPrefix,
        CrossComposerSubpieceNode ccn => ccn.CatalogSortPrefix ?? "￿",
        _                             => "￿",
    };

    private static int CatNumber(object o) => o switch
    {
        CanonPiece p                  => p.CatalogSortNumber,
        CrossComposerSubpieceNode ccn => ccn.CatalogSortNumber ?? int.MaxValue,
        _                             => int.MaxValue,
    };

    private static string CatSuffix(object o) => o switch
    {
        CanonPiece p                  => p.CatalogSortSuffix,
        CrossComposerSubpieceNode ccn => ccn.CatalogSortSuffix ?? "",
        _                             => "",
    };

    private static string Title(object o) => o switch
    {
        CanonPiece p                  => p.DisplayTitle ?? "",
        CrossComposerSubpieceNode ccn => ccn.DisplayTitle ?? "",
        _                             => "",
    };

    private static string Category(object o) => o switch
    {
        CanonPiece p                  => p.Category ?? "",
        CrossComposerSubpieceNode ccn => ccn.Category ?? "",
        _                             => "",
    };

    private static int Year(object o) => o switch
    {
        CanonPiece p                  => p.PublicationYear ?? int.MaxValue,
        CrossComposerSubpieceNode ccn => ccn.PublicationYear ?? int.MaxValue,
        _                             => int.MaxValue,
    };
}
