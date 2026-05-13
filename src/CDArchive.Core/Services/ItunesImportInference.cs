using System.Text.RegularExpressions;
using CDArchive.Core.Models;

namespace CDArchive.Core.Services;

/// <summary>
/// Pure parsing logic for iTunes import: composer field → name + dates, and
/// track <c>Name</c> field → piece title + a list of subpiece references with
/// sibling inference applied.
/// </summary>
public static class ItunesImportInference
{
    // ── Composer parsing ─────────────────────────────────────────────────────

    /// <summary>"LastName, FirstName (YYYY–YYYY)" or "(YYYY–)" (still-living).</summary>
    private static readonly Regex ComposerWithDatesRegex = new(
        @"^\s*(?<name>.+?)\s*\((?<birth>\d{3,4})\s*[–\-]\s*(?<death>\d{3,4})?\s*\)\s*$",
        RegexOptions.Compiled);

    public record ParsedComposer(string Name, int? BirthYear, int? DeathYear);

    /// <summary>
    /// Parses an iTunes composer field. Returns null when the field is null/empty.
    /// Falls back to the raw value (with no dates) when the dates pattern doesn't match.
    /// </summary>
    public static ParsedComposer? ParseComposer(string? composerField)
    {
        if (string.IsNullOrWhiteSpace(composerField)) return null;
        var trimmed = composerField.Trim();
        var match = ComposerWithDatesRegex.Match(trimmed);
        if (match.Success)
        {
            var name  = match.Groups["name"].Value.Trim();
            int? birth = int.TryParse(match.Groups["birth"].Value, out var b) ? b : null;
            int? death = match.Groups["death"].Success && int.TryParse(match.Groups["death"].Value, out var d) ? d : null;
            return new ParsedComposer(name, birth, death);
        }
        return new ParsedComposer(trimmed, null, null);
    }

    // ── Track Name parsing ───────────────────────────────────────────────────

    /// <summary>
    /// Matches an alphanumeric segment-number prefix like "1.", "2b.", "02c." followed by
    /// at least one space. The numeric part is captured; the trailing dot+spaces are consumed.
    /// </summary>
    private static readonly Regex NumberPrefixRegex = new(
        @"^(?<num>\d+[A-Za-z]*)\.\s+", RegexOptions.Compiled);

    /// <param name="MusicNumber">e.g. "1", "2b", "02c" — applies to the leaf (last) path component.</param>
    /// <param name="Path">Ordered list of subpiece titles from outermost down to leaf.</param>
    public record ParsedSubpieceRef(string? MusicNumber, IReadOnlyList<string> Path);

    public record ParsedTrackName(string PieceTitle, IReadOnlyList<ParsedSubpieceRef> SubpieceRefs);

    /// <summary>
    /// Parses an iTunes <c>Name</c> field using <c> - </c> (space-dash-space) as the segment
    /// separator at every depth. The first segment is the top-level piece title; subsequent
    /// segments are subpiece references.
    ///
    /// <para>Within a non-first segment:</para>
    /// <list type="bullet">
    ///   <item>Optional alphanumeric number prefix (e.g. <c>2b.</c>) is stripped and captured as
    ///     <see cref="ParsedSubpieceRef.MusicNumber"/>.</item>
    ///   <item>The remainder is split on <c>. </c> (period+space) into path components.</item>
    ///   <item><b>Sibling inference:</b> if a segment has only one component (no explicit parent
    ///     path), it inherits the previous segment's path with its last component replaced.</item>
    /// </list>
    ///
    /// <para>Example: <c>"Messa da Requiem - 2b. Dies irae. Tuba mirum - 02c. Mors stupebit"</c></para>
    /// <list type="bullet">
    ///   <item>PieceTitle = "Messa da Requiem"</item>
    ///   <item>SubpieceRefs[0] = (MusicNumber="2b", Path=["Dies irae","Tuba mirum"])</item>
    ///   <item>SubpieceRefs[1] = (MusicNumber="02c", Path=["Dies irae","Mors stupebit"])
    ///         — parent "Dies irae" inherited from previous</item>
    /// </list>
    ///
    /// <para><b>Known limitation:</b> the <c>. </c> split is naive — a path component containing
    /// a period followed by a space (e.g. <c>"Aria of St. Peter"</c>) will be over-split. Edit
    /// such tracks in iTunes or the piece tree directly after import.</para>
    /// </summary>
    public static ParsedTrackName ParseTrackName(string name)
    {
        if (string.IsNullOrEmpty(name)) return new ParsedTrackName("", Array.Empty<ParsedSubpieceRef>());

        var segments = name.Split(new[] { " - " }, StringSplitOptions.None);
        var pieceTitle = segments[0].Trim();

        var refs = new List<ParsedSubpieceRef>();
        IReadOnlyList<string>? previousPath = null;

        for (int i = 1; i < segments.Length; i++)
        {
            var segment = segments[i].Trim();
            if (segment.Length == 0) continue;

            string? musicNumber = null;
            var numMatch = NumberPrefixRegex.Match(segment);
            if (numMatch.Success)
            {
                musicNumber = numMatch.Groups["num"].Value;
                segment = segment[numMatch.Length..];
            }

            var components = segment
                .Split(new[] { ". " }, StringSplitOptions.None)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            IReadOnlyList<string> path;
            if (components.Count == 1 && previousPath is { Count: >= 1 })
            {
                // Sibling inference: take previous path, replace its last element.
                var inferred = new List<string>(previousPath.Take(previousPath.Count - 1)) { components[0] };
                path = inferred;
            }
            else
            {
                path = components;
            }

            if (path.Count == 0) continue;
            refs.Add(new ParsedSubpieceRef(musicNumber, path));
            previousPath = path;
        }

        return new ParsedTrackName(pieceTitle, refs);
    }
}
