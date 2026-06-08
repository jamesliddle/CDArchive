using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="ItunesImportInference.ParseTrackName"/> — the pure
/// parser that turns an iTunes <c>Name</c> field into a piece title and an
/// ordered list of <see cref="ItunesImportInference.ParsedSubpieceRef"/>s.
/// </summary>
public class ItunesImportInferenceTests
{
    /// <summary>Stand-in Forms pick list — the classifier now reads form names
    /// from the caller's curated list rather than a built-in lexicon, so the
    /// Phase-2 tests supply their own.</summary>
    private static readonly string[] Forms =
        { "Air", "Recitative", "Sinfonia", "Aria", "Chorus", "March" };

    // ── Phase 1: smart dot-tokenizer (ellipsis + abbreviation protection) ───

    [Fact]
    public void SplitDotComponents_NormalSeparators_Split()
    {
        Assert.Equal(
            new[] { "Dies irae", "Tuba mirum" },
            ItunesImportInference.SplitDotComponents("Dies irae. Tuba mirum"));

        Assert.Equal(
            new[] { "Scherzando", "Allegretto, ma non troppo" },
            ItunesImportInference.SplitDotComponents("Scherzando. Allegretto, ma non troppo"));

        // Three-way split (Phase 2 will classify these; Phase 1 just tokenizes).
        Assert.Equal(
            new[] { "Part I", "Air", "Every valley shall be exalted" },
            ItunesImportInference.SplitDotComponents("Part I. Air. Every valley shall be exalted"));
    }

    [Fact]
    public void SplitDotComponents_SpacedEllipsis_NotSplit()
    {
        // "Seis danzas afro-cubanas - 3. ¡ . . . Y la negra bailaba!" — after
        // the "3. " number prefix the segment is the ellipsis title. The
        // spaced ". . ." must stay intact as a single component.
        Assert.Equal(
            new[] { "¡ . . . Y la negra bailaba!" },
            ItunesImportInference.SplitDotComponents("¡ . . . Y la negra bailaba!"));
    }

    [Fact]
    public void SplitDotComponents_TightEllipsisAndUnicode_NotSplit()
    {
        // Tight "...Y" — dot followed by dot, never a separator.
        Assert.Equal(
            new[] { "...Y la negra bailaba!" },
            ItunesImportInference.SplitDotComponents("...Y la negra bailaba!"));

        // Unicode ellipsis is not a dot at all.
        Assert.Equal(
            new[] { "¡…Y la negra bailaba!" },
            ItunesImportInference.SplitDotComponents("¡…Y la negra bailaba!"));
    }

    [Fact]
    public void SplitDotComponents_Abbreviations_NotSplit()
    {
        // The documented over-split limitation: "Aria of St. Peter" must
        // stay one component.
        Assert.Equal(
            new[] { "Aria of St. Peter" },
            ItunesImportInference.SplitDotComponents("Aria of St. Peter"));

        Assert.Equal(
            new[] { "Variation No. 5" },
            ItunesImportInference.SplitDotComponents("Variation No. 5"));

        // Abbreviation mid-name doesn't suppress a genuine later separator.
        Assert.Equal(
            new[] { "St. Anne", "Fugue" },
            ItunesImportInference.SplitDotComponents("St. Anne. Fugue"));
    }

    [Fact]
    public void ParseTrackName_EllipsisTitle_StaysIntact()
    {
        // End-to-end: the ellipsis title survives parsing as a single leaf.
        var parsed = ItunesImportInference.ParseTrackName(
            "Seis danzas afro-cubanas - 3. ¡ . . . Y la negra bailaba!");

        Assert.Equal("Seis danzas afro-cubanas", parsed.PieceTitle);
        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("3", sub.MusicNumber);
        Assert.Equal(new[] { "¡ . . . Y la negra bailaba!" }, sub.Path);
    }

    [Fact]
    public void FindAmbiguousSegments_EllipsisTitle_NotFlagged()
    {
        // The ellipsis title is no longer over-split into multiple components,
        // so it's no longer flagged as ambiguous → no dialog for it.
        var ambiguous = ItunesImportInference.FindAmbiguousSegments(
            "Seis danzas afro-cubanas - 3. ¡ . . . Y la negra bailaba!");
        Assert.Empty(ambiguous);
    }

    // ── Phase 2: form-leaf classifier (mixed subpiece + form) ───────────────

    [Fact]
    public void ParseTrackName_MixedStructuralAndForm_Messiah()
    {
        // "Messiah - 03. Part I. Air. Every valley shall be exalted"
        //   Part I → structural subpiece
        //   Air    → leaf form
        //   Every valley shall be exalted → leaf title
        var parsed = ItunesImportInference.ParseTrackName(
            "Messiah - 03. Part I. Air. Every valley shall be exalted",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy, Forms);

        Assert.Equal("Messiah", parsed.PieceTitle);
        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("03", sub.MusicNumber);
        Assert.Equal(new[] { "Part I", "Every valley shall be exalted" }, sub.Path);
        Assert.Equal("Air", sub.LeafForm);
    }

    [Fact]
    public void ParseTrackName_StructuralHierarchyInherited_AcrossSegments_Beecham()
    {
        // "Messiah - 12b. Part I. Recitative. There were shepherds… -
        //           13a. Recitative. And lo, the angel of the Lord"
        // The user states the "Part I" hierarchy once on the first piece and
        // does not repeat it; the second piece inherits it.
        var parsed = ItunesImportInference.ParseTrackName(
            "Messiah - 12b. Part I. Recitative. There were shepherds abiding in the field" +
            " - 13a. Recitative. And lo, the angel of the Lord",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy, Forms);

        Assert.Equal("Messiah", parsed.PieceTitle);
        Assert.Equal(2, parsed.SubpieceRefs.Count);

        var first = parsed.SubpieceRefs[0];
        Assert.Equal("12b", first.MusicNumber);
        Assert.Equal(new[] { "Part I", "There were shepherds abiding in the field" }, first.Path);
        Assert.Equal("Recitative", first.LeafForm);

        var second = parsed.SubpieceRefs[1];
        Assert.Equal("13a", second.MusicNumber);
        // "Part I" inherited from the first segment.
        Assert.Equal(new[] { "Part I", "And lo, the angel of the Lord" }, second.Path);
        Assert.Equal("Recitative", second.LeafForm);
    }

    [Fact]
    public void ParseTrackName_CompoundForm_RecognisedAsSingleLeaf()
    {
        // "Part I. Air and Chorus. O thou that tellest…" — "Air and Chorus"
        // is a compound of two pick-list forms, so it's the leaf's (compound)
        // form, NOT a subpiece nesting over its own title.
        var parsed = ItunesImportInference.ParseTrackName(
            "Messiah - 08. Part I. Air and Chorus. O thou that tellest good tidings to Zion",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy, Forms);

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("08", sub.MusicNumber);
        Assert.Equal(new[] { "Part I", "O thou that tellest good tidings to Zion" }, sub.Path);
        Assert.Equal("Air and Chorus", sub.LeafForm);
    }

    [Fact]
    public void ParseTrackName_BareFormLeaf_NoStructuralPrefix()
    {
        // "Air. Every valley shall be exalted" — no Part prefix. The classifier
        // still treats it as one leaf (Form=Air), NOT two nested subpieces
        // (which plain SubpieceHierarchy would wrongly produce).
        var parsed = ItunesImportInference.ParseTrackName(
            "Messiah - 9. Air. Every valley shall be exalted",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy, Forms);

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal(new[] { "Every valley shall be exalted" }, sub.Path);
        Assert.Equal("Air", sub.LeafForm);
    }

    [Fact]
    public void ParseTrackName_StructuralPrefix_NonFormLeaf_NestsAsHierarchy()
    {
        // "Part I. Tuba mirum" — structural prefix + non-form leaf. The leaf
        // isn't a known form, so it nests as a plain subpiece (no LeafForm).
        var parsed = ItunesImportInference.ParseTrackName(
            "Requiem - 2. Part I. Tuba mirum");

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal(new[] { "Part I", "Tuba mirum" }, sub.Path);
        Assert.Null(sub.LeafForm);
    }

    [Fact]
    public void ParseTrackName_NonFormTwoComponents_FallsThroughToInterpretation()
    {
        // "Dies irae. Tuba mirum" — neither token is a known form and there's
        // no structural prefix, so the classifier does NOT fire; the binary
        // SubpieceHierarchy interpretation produces two nested subpieces.
        var parsed = ItunesImportInference.ParseTrackName(
            "Requiem - 2. Dies irae. Tuba mirum",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy);

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal(new[] { "Dies irae", "Tuba mirum" }, sub.Path);
        Assert.Null(sub.LeafForm);
    }

    [Fact]
    public void FindAmbiguousSegments_ClassifierResolved_NotFlagged()
    {
        // The mixed/form-leaf shapes the classifier handles must NOT surface
        // the subpiece-vs-form-and-tempo dialog.
        Assert.Empty(ItunesImportInference.FindAmbiguousSegments(
            "Messiah - 03. Part I. Air. Every valley shall be exalted", Forms));
        Assert.Empty(ItunesImportInference.FindAmbiguousSegments(
            "Messiah - 9. Air. Every valley shall be exalted", Forms));

        // But a genuinely ambiguous non-form segment is still flagged.
        Assert.NotEmpty(ItunesImportInference.FindAmbiguousSegments(
            "Requiem - 2. Dies irae. Tuba mirum", Forms));
    }

    [Fact]
    public void ClassifyFormSegment_FormOnlyLeafUnderStructural()
    {
        // "Part I. Sinfonia" — form-only leaf (no title text). Structural part
        // holds "Part I"; the form word stands in as the leaf; LeafForm set.
        var formSet = new HashSet<string>(Forms, StringComparer.OrdinalIgnoreCase);
        var result = ItunesImportInference.ClassifyFormSegment(
            new[] { "Part I", "Sinfonia" }, formSet);
        Assert.NotNull(result);
        Assert.Equal(new[] { "Part I" }, result!.StructuralPrefixes);
        Assert.Equal(new[] { "Sinfonia" }, result.LeafPath);
        Assert.Equal("Sinfonia", result.LeafForm);
    }

    [Fact]
    public void ClassifyFormSegment_SingleNonFormToken_ReturnsNull()
    {
        var formSet = new HashSet<string>(Forms, StringComparer.OrdinalIgnoreCase);
        // A single ordinary token isn't a classifier case.
        Assert.Null(ItunesImportInference.ClassifyFormSegment(new[] { "Allegro" }, formSet));
        // A single form word alone ("Aria") also doesn't fire — left to the
        // existing single-component handling so we don't change that behaviour.
        Assert.Null(ItunesImportInference.ClassifyFormSegment(new[] { "Aria" }, formSet));
    }

    // ── Baseline cases ────────────────────────────────────────────────────

    [Fact]
    public void ParseTrackName_NoSubpieces_ReturnsPieceTitleOnly()
    {
        var parsed = ItunesImportInference.ParseTrackName("Souvenir d'une nuit d'été à Madrid");

        Assert.Equal("Souvenir d'une nuit d'été à Madrid", parsed.PieceTitle);
        Assert.Empty(parsed.SubpieceRefs);
    }

    [Fact]
    public void ParseTrackName_SingleNumberedSubpiece()
    {
        var parsed = ItunesImportInference.ParseTrackName("Symphony - 1. Allegro");

        Assert.Equal("Symphony", parsed.PieceTitle);
        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("1", sub.MusicNumber);
        Assert.Equal(new[] { "Allegro" }, sub.Path);
        Assert.Null(sub.Tempos);   // single-tempo movement
    }

    [Fact]
    public void ParseTrackName_TwoNumberedSubpieces_ProducesTwoSiblings()
    {
        var parsed = ItunesImportInference.ParseTrackName("Symphony - 1. Allegro - 2. Andante");

        Assert.Equal(2, parsed.SubpieceRefs.Count);
        Assert.Equal("1", parsed.SubpieceRefs[0].MusicNumber);
        Assert.Equal(new[] { "Allegro" }, parsed.SubpieceRefs[0].Path);
        Assert.Equal("2", parsed.SubpieceRefs[1].MusicNumber);
        Assert.Equal(new[] { "Andante" }, parsed.SubpieceRefs[1].Path);
    }

    // ── Tempo continuation (the user-requested feature) ──────────────────

    [Fact]
    public void ParseTrackName_NumberedThenUnnumbered_GroupsAsSingleMultiTempoMovement()
    {
        // The headline case from the user's bug report:
        // "Symphony #11 in B-flat, Op. 34 - 1. Lento - Allegro agitato"
        // should become ONE subpiece (movement #1) with two tempi, not two
        // separate sibling subpieces.
        var parsed = ItunesImportInference.ParseTrackName(
            "Symphony #11 in B-flat, Op. 34 - 1. Lento - Allegro agitato");

        Assert.Equal("Symphony #11 in B-flat, Op. 34", parsed.PieceTitle);
        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("1", sub.MusicNumber);
        Assert.Equal(new[] { "Lento - Allegro agitato" }, sub.Path);
        Assert.NotNull(sub.Tempos);
        Assert.Equal(new[] { "Lento", "Allegro agitato" }, sub.Tempos);
    }

    [Fact]
    public void ParseTrackName_NumberedThenTwoUnnumbered_GroupsAllThreeTempi()
    {
        // Three consecutive tempi on movement 1.
        var parsed = ItunesImportInference.ParseTrackName(
            "Piece - 1. Adagio - Allegro - Presto");

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("1", sub.MusicNumber);
        Assert.Equal(new[] { "Adagio - Allegro - Presto" }, sub.Path);
        Assert.Equal(new[] { "Adagio", "Allegro", "Presto" }, sub.Tempos);
    }

    [Fact]
    public void ParseTrackName_MultiTempoMovementThenSiblingMovement_BothTracked()
    {
        // Movement 1 is multi-tempo; movement 2 starts a new numbered movement.
        var parsed = ItunesImportInference.ParseTrackName(
            "Piece - 1. Lento - Allegro - 2. Andante");

        Assert.Equal(2, parsed.SubpieceRefs.Count);

        Assert.Equal("1", parsed.SubpieceRefs[0].MusicNumber);
        Assert.Equal(new[] { "Lento - Allegro" }, parsed.SubpieceRefs[0].Path);
        Assert.Equal(new[] { "Lento", "Allegro" }, parsed.SubpieceRefs[0].Tempos);

        Assert.Equal("2", parsed.SubpieceRefs[1].MusicNumber);
        Assert.Equal(new[] { "Andante" }, parsed.SubpieceRefs[1].Path);
        Assert.Null(parsed.SubpieceRefs[1].Tempos);
    }

    [Fact]
    public void ParseTrackName_UnnumberedBetweenNumbered_GroupsWithPrevious()
    {
        // "1. Lento - Allegro - 2. Andante - 3. Presto" — Lento+Allegro
        // group on #1; #2 and #3 stand alone.
        var parsed = ItunesImportInference.ParseTrackName(
            "Piece - 1. Lento - Allegro - 2. Andante - 3. Presto");

        Assert.Equal(3, parsed.SubpieceRefs.Count);
        Assert.Equal(new[] { "Lento", "Allegro" }, parsed.SubpieceRefs[0].Tempos);
        Assert.Equal("2", parsed.SubpieceRefs[1].MusicNumber);
        Assert.Equal("3", parsed.SubpieceRefs[2].MusicNumber);
    }

    // ── Cases that should NOT trigger tempo grouping ──────────────────────

    [Fact]
    public void ParseTrackName_AllUnnumbered_PreservesSiblingInference_NotTempoGrouping()
    {
        // No number prefix anywhere → sibling inference, NOT tempo grouping.
        // Existing pre-fix behaviour preserved.
        var parsed = ItunesImportInference.ParseTrackName("Piece - Lento - Allegro");

        Assert.Equal(2, parsed.SubpieceRefs.Count);
        Assert.Null(parsed.SubpieceRefs[0].MusicNumber);
        Assert.Null(parsed.SubpieceRefs[0].Tempos);
        Assert.Equal(new[] { "Lento" },   parsed.SubpieceRefs[0].Path);
        Assert.Equal(new[] { "Allegro" }, parsed.SubpieceRefs[1].Path);
    }

    [Fact]
    public void ParseTrackName_NumberedNested_SiblingInference_NotTempoGrouping()
    {
        // The classical sibling-inference example from the docstring:
        // "Messa da Requiem - 2b. Dies irae. Tuba mirum - 02c. Mors stupebit"
        // BOTH segments after the title have number prefixes — tempo grouping
        // does NOT fire; sibling inference (replace last path component) does.
        var parsed = ItunesImportInference.ParseTrackName(
            "Messa da Requiem - 2b. Dies irae. Tuba mirum - 02c. Mors stupebit");

        Assert.Equal(2, parsed.SubpieceRefs.Count);
        Assert.Equal("2b",  parsed.SubpieceRefs[0].MusicNumber);
        Assert.Equal(new[] { "Dies irae", "Tuba mirum" }, parsed.SubpieceRefs[0].Path);
        Assert.Equal("02c", parsed.SubpieceRefs[1].MusicNumber);
        Assert.Equal(new[] { "Dies irae", "Mors stupebit" }, parsed.SubpieceRefs[1].Path);
        Assert.Null(parsed.SubpieceRefs[0].Tempos);
        Assert.Null(parsed.SubpieceRefs[1].Tempos);
    }

    /// <summary>
    /// FormAndTempo interpretation collapses a <c>". "</c>-split segment to
    /// ONE leaf whose Path component is the unsplit segment and whose Tempos
    /// list carries the components. This is the instrumental-sonata reading
    /// the user requested in the import dialog ("Scherzando. Allegretto, ma
    /// non troppo" = one movement, form "Scherzando", tempo
    /// "Allegretto, ma non troppo").
    /// </summary>
    [Fact]
    public void ParseTrackName_FormAndTempo_CollapsesSegmentToOneLeafWithTempos()
    {
        var parsed = ItunesImportInference.ParseTrackName(
            "Piano Sonata in D, WoO 47 #3 - 3. Scherzando. Allegretto, ma non troppo",
            ItunesImportInference.DotSeparatorInterpretation.FormAndTempo);

        Assert.Equal("Piano Sonata in D, WoO 47 #3", parsed.PieceTitle);
        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal("3", sub.MusicNumber);
        Assert.Equal(new[] { "Scherzando. Allegretto, ma non troppo" }, sub.Path);
        Assert.Equal(new[] { "Scherzando", "Allegretto, ma non troppo" }, sub.Tempos);
    }

    /// <summary>
    /// SubpieceHierarchy (the default and pre-existing behaviour) keeps the
    /// segment split into multiple Path components — the Verdi-Requiem
    /// reading. This is the same input as the test above with the
    /// interpretation flipped; the output diverges so the contract is
    /// double-pinned.
    /// </summary>
    [Fact]
    public void ParseTrackName_SubpieceHierarchy_KeepsSegmentSplitAsPath()
    {
        var parsed = ItunesImportInference.ParseTrackName(
            "Piano Sonata in D, WoO 47 #3 - 3. Scherzando. Allegretto, ma non troppo",
            ItunesImportInference.DotSeparatorInterpretation.SubpieceHierarchy);

        var sub = Assert.Single(parsed.SubpieceRefs);
        Assert.Equal(new[] { "Scherzando", "Allegretto, ma non troppo" }, sub.Path);
        Assert.Null(sub.Tempos);
    }

    /// <summary>
    /// FindAmbiguousSegments returns the segments-after-number-prefix that
    /// contain <c>". "</c> — exactly the segments the dialog should ask the
    /// user about. Non-ambiguous names produce an empty result.
    /// </summary>
    [Fact]
    public void FindAmbiguousSegments_PicksUpDotSeparatorSegments_AndSkipsCleanOnes()
    {
        Assert.Empty(ItunesImportInference.FindAmbiguousSegments(
            "Piano Sonata in f, WoO 47 #2 - 2. Andante"));

        var found = ItunesImportInference.FindAmbiguousSegments(
            "Piano Sonata in D, WoO 47 #3 - 3. Scherzando. Allegretto, ma non troppo");
        var seg = Assert.Single(found);
        Assert.Equal("Scherzando. Allegretto, ma non troppo", seg.Segment);
        Assert.Equal(new[] { "Scherzando", "Allegretto, ma non troppo" }, seg.Components);
    }
}
