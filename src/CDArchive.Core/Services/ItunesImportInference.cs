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

    /// <summary>
    /// Matches a genuine <c>. </c> component separator, excluding the cases
    /// the old naive <c>Split(". ")</c> over-split on:
    /// <list type="bullet">
    ///   <item><b>Ellipsis</b> — a dot preceded by a dot or space (the spaced
    ///     <c>. . .</c> form), or followed by another dot (the tight <c>...</c>
    ///     form), is never a separator. So
    ///     <c>"¡ . . . Y la negra bailaba!"</c> stays one component.</item>
    ///   <item><b>Abbreviations</b> — a dot immediately following a known
    ///     title abbreviation (<c>No.</c>, <c>St.</c>, <c>Mr.</c>, etc.) is
    ///     never a separator. So <c>"Aria of St. Peter"</c> stays intact —
    ///     fixing the documented over-split limitation.</item>
    /// </list>
    /// A separator is therefore: a single dot, not part of a multi-dot run,
    /// not closing a known abbreviation, followed by at least one space.
    /// .NET's variable-length lookbehind makes the abbreviation guard
    /// expressible inline.
    /// </summary>
    private static readonly Regex DotSeparatorRegex = new(
        @"(?<![.\s])(?<!\b(?:No|Nos|St|Ste|Mr|Mrs|Ms|Dr|vs|Op|Opp))\.\s+(?!\.)",
        RegexOptions.Compiled);

    /// <summary>
    /// Splits a segment (after the number prefix has been stripped) into its
    /// <c>. </c>-separated components using <see cref="DotSeparatorRegex"/>,
    /// which protects ellipses and abbreviations. Trims each component and
    /// drops empties — same post-processing the call sites used with the old
    /// naive split.
    /// </summary>
    internal static IReadOnlyList<string> SplitDotComponents(string segment)
    {
        if (string.IsNullOrEmpty(segment)) return Array.Empty<string>();
        return DotSeparatorRegex.Split(segment)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    // ── Form-leaf classifier (Phase 2) ───────────────────────────────────────

    /// <summary>
    /// Structural-division prefix: <c>Part I</c>, <c>Act 2</c>, <c>Scene 3</c>,
    /// <c>Book I</c>, <c>Tableau 2</c>, <c>No. 5</c>. A dot-token matching this
    /// is a hierarchy level (subpiece), not a leaf — so the classifier nests it
    /// above the leaf rather than collapsing it.
    /// </summary>
    private static readonly Regex StructuralPrefixRegex = new(
        @"^(Part|Act|Scene|Tableau|Book|Volume|No\.?)\s+[\dIVXLCDM]+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Splits a candidate compound-form token ("Air and Chorus", "Recitative &amp;
    /// Aria", "Accompagnato, Recitative and Chorus") into its parts on
    /// <c> and </c>, <c> &amp; </c>, or comma.
    /// </summary>
    private static readonly Regex CompoundFormSplitRegex = new(
        @"\s+(?:and|&)\s+|\s*,\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// True when <paramref name="token"/> is a compound of two or more
    /// pick-list forms joined by "and" / "&amp;" / comma — e.g. "Air and
    /// Chorus" where both "Air" and "Chorus" are in the Forms list. The whole
    /// token is then treated as a single (compound) leaf form, so the movement
    /// becomes Form="Air and Chorus" / Title=&lt;the following text&gt; rather
    /// than nesting "Air and Chorus" as a subpiece over its own title.
    /// </summary>
    private static bool IsCompoundForm(string token, IReadOnlySet<string> forms)
    {
        var parts = CompoundFormSplitRegex.Split(token)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count >= 2 && parts.All(forms.Contains);
    }

    /// <summary>
    /// Result of <see cref="ClassifyFormSegment"/>: a segment split into its
    /// leading structural prefixes (hierarchy levels), the leaf path tokens,
    /// and the leaf's form (when it led with a recognised form word).
    /// Keeping the structural and leaf parts separate lets the parse loop
    /// inherit a previous segment's structural hierarchy when a later segment
    /// omits it.
    /// </summary>
    internal sealed record FormSegmentClassification(
        IReadOnlyList<string> StructuralPrefixes,
        IReadOnlyList<string> LeafPath,
        string? LeafForm);

    /// <summary>
    /// Classifies a segment's dot-tokens into a mixed
    /// <c>[structural subpieces…] + [form leaf]</c> shape, returning null when
    /// the segment isn't of that form (so the caller falls through to the
    /// binary <see cref="DotSeparatorInterpretation"/> logic).
    ///
    /// <para>Form recognition is driven by <paramref name="forms"/> — the
    /// caller's curated Forms pick list, NOT a built-in lexicon. When the set
    /// is null/empty only the structural-prefix cases fire.</para>
    ///
    /// <para>Fires only when there's genuine structure the binary
    /// interpretations mishandle:</para>
    /// <list type="bullet">
    ///   <item>A structural prefix (<c>Part I</c>) is present — the mixed case.</item>
    ///   <item>OR the (post-prefix) leaf leads with a pick-list form AND has at
    ///     least one more token of title text ("Air. Every valley…") — the
    ///     case SubpieceHierarchy would wrongly nest and FormAndTempo would
    ///     wrongly treat as tempos.</item>
    /// </list>
    /// </summary>
    internal static FormSegmentClassification? ClassifyFormSegment(
        IReadOnlyList<string> components,
        IReadOnlySet<string>? forms)
    {
        if (components.Count == 0) return null;

        int i = 0;
        var structural = new List<string>();
        while (i < components.Count && StructuralPrefixRegex.IsMatch(components[i]))
            structural.Add(components[i++]);

        var rest = components.Skip(i).ToList();
        if (rest.Count == 0)
            return null; // nothing but structural prefixes — let existing logic handle.

        // The leaf leads with a form when its first token is a pick-list form,
        // OR a compound of pick-list forms ("Air and Chorus"). The compound
        // text is preserved verbatim as the leaf's form.
        var leadIsForm = forms is not null
            && (forms.Contains(rest[0]) || IsCompoundForm(rest[0], forms));

        // Decide whether to fire. Two qualifying shapes:
        //   (a) structural prefix present → the genuine mixed case.
        //   (b) no structural prefix, but leaf is "Form. <text…>" (≥2 tokens).
        var fires = structural.Count > 0
            ? rest.Count >= 1                       // any leaf under a structural prefix
            : leadIsForm && rest.Count >= 2;        // "Air. Every valley…" only
        if (!fires) return null;

        if (leadIsForm && rest.Count >= 2)
        {
            // "Air. Every valley shall be exalted" → Form=Air, leaf title=rest.
            return new FormSegmentClassification(
                structural,
                new[] { string.Join(". ", rest.Skip(1)) },
                rest[0]);
        }
        if (leadIsForm && rest.Count == 1)
        {
            // Form-only leaf (e.g. "Part I. Sinfonia"). The form word stands
            // in as the leaf path element; EnsureSubpiecePath clears the title.
            return new FormSegmentClassification(structural, new[] { rest[0] }, rest[0]);
        }
        // Structural prefix(es) + a non-form leaf ("Part I. Tuba mirum" or
        // "Part I. Dies irae. Tuba mirum"): keep the leaf tokens nested, no form.
        return new FormSegmentClassification(structural, rest, null);
    }

    /// <param name="MusicNumber">e.g. "1", "2b", "02c" — applies to the leaf (last) path component.</param>
    /// <param name="Path">Ordered list of subpiece titles from outermost down to leaf.</param>
    /// <param name="Tempos">When set (count &gt; 1), the leaf subpiece is a multi-tempo
    ///   movement — each entry is one tempo description in order. The leaf path
    ///   component is the joined tempi (e.g. <c>"Lento - Allegro agitato"</c>).
    ///   Null or single-element means a regular single-tempo subpiece (the
    ///   leaf's title is its only tempo, conventionally implicit).</param>
    /// <param name="TemposFromFormCollapse">True when <see cref="Tempos"/> was
    ///   produced by the FormAndTempo interpretation collapsing a "Form. Tempo"
    ///   segment — so <c>Tempos[0]</c> is the FORM, not a tempo. The importer
    ///   uses this to avoid writing the form onto a matched curated movement as
    ///   a bogus tempo marker.</param>
    public record ParsedSubpieceRef(
        string? MusicNumber,
        IReadOnlyList<string> Path,
        IReadOnlyList<string>? Tempos = null,
        bool TemposFromFormCollapse = false,
        // Set by the form-leaf classifier when the leaf segment led with a
        // recognised musical form (e.g. "Air. Every valley shall be exalted"
        // → LeafForm="Air", leaf Path element="Every valley shall be exalted").
        // The importer applies this as the created leaf's Form so the canon
        // movement is Form=Air / Title="Every valley…" rather than a title
        // with the form jammed in.
        string? LeafForm = null);

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
    /// <summary>
    /// How to interpret a <c>. </c> separator within a single iTunes
    /// segment after the number prefix. The default
    /// <see cref="SubpieceHierarchy"/> matches the historical Verdi-Requiem
    /// shape ("2b. Dies irae. Tuba mirum" → Dies irae &gt; Tuba mirum).
    /// <see cref="FormAndTempo"/> matches the instrumental-sonata shape
    /// ("3. Scherzando. Allegretto" → one movement, form "Scherzando",
    /// tempo "Allegretto") — the piece becomes a single subpiece whose
    /// title is the joined segment and whose Tempos list carries the
    /// individual components.
    /// </summary>
    public enum DotSeparatorInterpretation
    {
        SubpieceHierarchy,
        FormAndTempo,
    }

    /// <summary>
    /// One ambiguous segment found in an iTunes track name — used by
    /// <see cref="FindAmbiguousSegments"/> to drive the import-time
    /// confirmation dialog.
    /// </summary>
    /// <param name="Segment">The full segment after the number prefix
    /// (e.g. <c>"Scherzando. Allegretto, ma non troppo"</c>).</param>
    /// <param name="Components">The <c>. </c>-split components (e.g.
    /// <c>["Scherzando", "Allegretto, ma non troppo"]</c>).</param>
    public record AmbiguousSegment(string Segment, IReadOnlyList<string> Components);

    /// <summary>
    /// Scans a track name for segments that contain <c>. </c> after the
    /// number prefix — the ambiguous shape that could mean either a
    /// subpiece hierarchy (Requiem-style) or a form-and-tempo combination
    /// (instrumental-sonata style). Returns one entry per such segment;
    /// empty when the name has no ambiguous segments.
    /// </summary>
    public static IReadOnlyList<AmbiguousSegment> FindAmbiguousSegments(
        string name, IReadOnlyCollection<string>? forms = null)
    {
        if (string.IsNullOrEmpty(name)) return Array.Empty<AmbiguousSegment>();

        var formSet = BuildFormSet(forms);
        var segments = name.Split(new[] { " - " }, StringSplitOptions.None);
        var result = new List<AmbiguousSegment>();

        for (int i = 1; i < segments.Length; i++)
        {
            var raw = segments[i].Trim();
            if (raw.Length == 0) continue;

            var segment = raw;
            var numMatch = NumberPrefixRegex.Match(segment);
            if (numMatch.Success)
                segment = segment[numMatch.Length..];

            var components = SplitDotComponents(segment);

            // Segments the form-leaf classifier resolves deterministically
            // (mixed structural+form, or "Form. <text>") are NOT ambiguous —
            // they don't need the subpiece-vs-form-and-tempo dialog.
            if (components.Count > 1 && ClassifyFormSegment(components, formSet) is null)
                result.Add(new AmbiguousSegment(segment, components.ToList()));
        }

        return result;
    }

    /// <summary>Builds a case-insensitive set from the caller's Forms pick
    /// list, or null when no forms were supplied.</summary>
    private static IReadOnlySet<string>? BuildFormSet(IReadOnlyCollection<string>? forms)
        => forms is { Count: > 0 }
            ? new HashSet<string>(forms, StringComparer.OrdinalIgnoreCase)
            : null;

    public static ParsedTrackName ParseTrackName(string name)
        => ParseTrackName(name, DotSeparatorInterpretation.SubpieceHierarchy);

    /// <summary>
    /// Overload that lets the caller pick how to interpret <c>. </c>
    /// separators within a segment (see <see cref="DotSeparatorInterpretation"/>).
    /// The interpretation applies to every ambiguous segment in this name;
    /// callers that want per-segment control should resolve the choice
    /// upstream (e.g. via the iTunes-import dialog) and pass a single
    /// effective value per track.
    /// </summary>
    public static ParsedTrackName ParseTrackName(string name, DotSeparatorInterpretation dotInterpretation)
        => ParseTrackName(name, dotInterpretation, forms: null);

    /// <summary>
    /// Full overload: <paramref name="forms"/> is the caller's Forms pick list,
    /// used by the form-leaf classifier to recognise vocal/structural forms
    /// (Air, Recitative, …) within a segment. Null disables form recognition
    /// (only structural-prefix cases classify).
    /// </summary>
    public static ParsedTrackName ParseTrackName(
        string name,
        DotSeparatorInterpretation dotInterpretation,
        IReadOnlyCollection<string>? forms)
    {
        if (string.IsNullOrEmpty(name)) return new ParsedTrackName("", Array.Empty<ParsedSubpieceRef>());

        var formSet = BuildFormSet(forms);
        var segments = name.Split(new[] { " - " }, StringSplitOptions.None);
        var pieceTitle = segments[0].Trim();

        var refs = new List<ParsedSubpieceRef>();
        IReadOnlyList<string>? previousPath = null;
        // Structural hierarchy (e.g. ["Part I"]) carried from the most recent
        // classified segment that declared one. A later classified segment
        // that omits its own structural prefix inherits this — the user's
        // convention is to state the hierarchy once and not repeat it for
        // subsequent pieces under the same hierarchy.
        IReadOnlyList<string>? previousStructural = null;

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

            var components = SplitDotComponents(segment).ToList();

            // Form-leaf classifier (Phase 2): takes precedence over the binary
            // interpretation when it recognises a mixed structural+form shape
            // ("Part I. Air. Every valley shall be exalted") or a bare
            // "Form. <text>" leaf ("Air. Every valley shall be exalted"). It
            // produces the correct nesting deterministically, so neither
            // dotInterpretation applies to these segments.
            var classified = ClassifyFormSegment(components, formSet);
            if (classified is { } cls)
            {
                // Structural-hierarchy inheritance: a classified segment that
                // omits its own structural prefix inherits the previous one
                // (the user states "Part I" once, not on every subsequent
                // piece under it). A segment that declares its own structural
                // prefix replaces the inherited one.
                var structural = cls.StructuralPrefixes;
                if (structural.Count == 0 && previousStructural is { Count: > 0 })
                    structural = previousStructural;
                else if (structural.Count > 0)
                    previousStructural = structural;

                var fullPath = structural.Concat(cls.LeafPath).ToList();
                refs.Add(new ParsedSubpieceRef(
                    musicNumber, fullPath, Tempos: null,
                    TemposFromFormCollapse: false, LeafForm: cls.LeafForm));
                previousPath = fullPath;
                continue;
            }

            // FormAndTempo: collapse a multi-component segment to ONE leaf
            // whose title is the unsplit segment and whose Tempos list
            // carries the split components. The caller picked this when the
            // segment is instrumental-style ("Scherzando. Allegretto, ma non
            // troppo" — one movement) rather than vocal-style
            // ("Dies irae. Tuba mirum" — two subpieces).
            if (dotInterpretation == DotSeparatorInterpretation.FormAndTempo
                && components.Count > 1)
            {
                var leaf = segment.Trim();
                var tempos = components;
                // TemposFromFormCollapse: components[0] is the FORM, not a tempo
                // — the importer must not write these onto a matched curated
                // movement as tempo markers.
                refs.Add(new ParsedSubpieceRef(musicNumber, new[] { leaf }, tempos,
                    TemposFromFormCollapse: true));
                previousPath = new[] { leaf };
                continue;
            }

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
                // Preserve the form-collapse flag: when prev came from a
                // FormAndTempo collapse (its Tempos[0] is the form), a trailing
                // continuation must stay flagged so the importer still won't
                // write those values onto a matched curated movement.
                refs[^1] = new ParsedSubpieceRef(
                    prev.MusicNumber, newPath, existing, prev.TemposFromFormCollapse);
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
