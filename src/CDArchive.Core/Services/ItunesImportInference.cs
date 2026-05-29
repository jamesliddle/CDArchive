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

    /// <summary>
    /// Boundary marker between principal and contributor credits in an iTunes
    /// composer field, e.g. <c>", compl. Franco Alfano (1875–1954)"</c>. The
    /// supported role abbreviations match the examples documented on
    /// <see cref="ComposerCredit.Role"/>: arranger / orchestrator / transcriber /
    /// completer / editor / reviser.
    /// </summary>
    private static readonly Regex CreditBoundaryRegex = new(
        @",\s*(?<role>compl|arr|orch|transcr|ed|rev)\.\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public record ParsedContributorCredit(string Name, int? BirthYear, int? DeathYear, string Role);

    public record ParsedComposer(
        string Name,
        int? BirthYear,
        int? DeathYear,
        IReadOnlyList<ParsedContributorCredit>? Contributors = null);

    /// <summary>
    /// Parses an iTunes composer field. Returns null when the field is null/empty.
    /// Falls back to the raw value (with no dates) when the dates pattern doesn't match.
    /// <para>
    /// Recognises compound credits of the form
    /// <c>"Principal (YYYY–YYYY), ROLE. Contributor (YYYY–YYYY)"</c>, where
    /// <c>ROLE</c> is one of the abbreviations in <see cref="CreditBoundaryRegex"/>.
    /// Each contributor's name is normalised to the canon's surname-first form
    /// when the iTunes field gave it as given-name-first (e.g.
    /// <c>"Franco Alfano"</c> → <c>"Alfano, Franco"</c>); already-surname-first
    /// names like <c>"Busoni, Ferruccio"</c> are left untouched.
    /// </para>
    /// </summary>
    public static ParsedComposer? ParseComposer(string? composerField)
    {
        if (string.IsNullOrWhiteSpace(composerField)) return null;
        var trimmed = composerField.Trim();

        var boundaries = CreditBoundaryRegex.Matches(trimmed);
        if (boundaries.Count == 0)
            return ParsePrincipalSegment(trimmed);

        // Principal: everything up to the first boundary.
        var principalText = trimmed.Substring(0, boundaries[0].Index).Trim();
        var principal = ParsePrincipalSegment(principalText);
        if (principal is null) return null;

        var contributors = new List<ParsedContributorCredit>();
        for (int i = 0; i < boundaries.Count; i++)
        {
            var role  = boundaries[i].Groups["role"].Value.ToLowerInvariant() + ".";
            var start = boundaries[i].Index + boundaries[i].Length;
            var end   = (i + 1 < boundaries.Count) ? boundaries[i + 1].Index : trimmed.Length;
            var text  = trimmed.Substring(start, end - start).Trim();
            if (text.Length == 0) continue;

            var parsed = ParsePrincipalSegment(text);
            if (parsed is null) continue;

            var normalized = NormalizeContributorName(parsed.Name);
            contributors.Add(new ParsedContributorCredit(
                normalized, parsed.BirthYear, parsed.DeathYear, role));
        }

        return principal with
        {
            Contributors = contributors.Count > 0 ? contributors : null,
        };
    }

    private static ParsedComposer? ParsePrincipalSegment(string text)
    {
        if (text.Length == 0) return null;
        var match = ComposerWithDatesRegex.Match(text);
        if (match.Success)
        {
            var name  = match.Groups["name"].Value.Trim();
            int? birth = int.TryParse(match.Groups["birth"].Value, out var b) ? b : null;
            int? death = match.Groups["death"].Success &&
                         int.TryParse(match.Groups["death"].Value, out var d) ? d : null;
            return new ParsedComposer(name, birth, death);
        }
        return new ParsedComposer(text, null, null);
    }

    /// <summary>
    /// Flips a contributor name to surname-first when iTunes gave it as
    /// given-name-first. A comma in the name is treated as "already
    /// surname-first" and left alone. The heuristic — take the last
    /// whitespace-separated token as the surname — is wrong for compound
    /// surnames like "van Beethoven" or "De Sabata"; the user can edit the
    /// CanonComposer after import in those cases.
    /// </summary>
    private static string NormalizeContributorName(string name)
    {
        if (name.Contains(',')) return name;
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return name;
        var surname = parts[^1];
        var given   = string.Join(' ', parts.Take(parts.Length - 1));
        return $"{surname}, {given}";
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
    /// <param name="Tempos">When set (count &gt; 1), the leaf subpiece is a multi-tempo
    ///   movement — each entry is one tempo description in order. The leaf path
    ///   component is the joined tempi (e.g. <c>"Lento - Allegro agitato"</c>).
    ///   Null or single-element means a regular single-tempo subpiece (the
    ///   leaf's title is its only tempo, conventionally implicit).</param>
    public record ParsedSubpieceRef(
        string? MusicNumber,
        IReadOnlyList<string> Path,
        IReadOnlyList<string>? Tempos = null);

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
    /// <para><b>Tempo continuation:</b> when an unnumbered segment follows a
    /// numbered single-component segment, it's treated as an additional tempo
    /// of the same movement rather than a sibling. The leaf path component
    /// becomes the joined tempi (matching the canon convention for
    /// multi-tempo movements like Chopin's "Winter Wind").</para>
    ///
    /// <para>Example: <c>"Symphony #11 in B-flat, Op. 34 - 1. Lento - Allegro agitato"</c></para>
    /// <list type="bullet">
    ///   <item>PieceTitle = "Symphony #11 in B-flat, Op. 34"</item>
    ///   <item>SubpieceRefs[0] = (MusicNumber="1", Path=["Lento - Allegro agitato"],
    ///         Tempos=["Lento","Allegro agitato"])</item>
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

            // Tempo continuation: an unnumbered single-component segment
            // following a numbered single-component movement gets folded into
            // the previous ref as an additional tempo, rather than becoming a
            // sibling. Matches the iTunes convention
            //   "Piece - 1. Lento - Allegro agitato"
            // describing ONE movement (#1) with two consecutive tempi.
            if (musicNumber is null && components.Count == 1 &&
                refs.Count > 0 && refs[^1].MusicNumber is not null &&
                refs[^1].Path.Count == 1)
            {
                var prev = refs[^1];
                // Seed Tempos with the previous leaf's title when this is the
                // first continuation; subsequent ones append.
                var existing = prev.Tempos is { Count: > 0 }
                    ? new List<string>(prev.Tempos)
                    : new List<string> { prev.Path[0] };
                existing.Add(components[0]);
                var joined = string.Join(" - ", existing);
                var newPath = new List<string> { joined };
                refs[^1] = new ParsedSubpieceRef(prev.MusicNumber, newPath, existing);
                previousPath = newPath;
                continue;
            }

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
