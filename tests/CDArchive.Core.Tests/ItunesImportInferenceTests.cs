using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="ItunesImportInference.ParseTrackName"/> — the pure
/// parser that turns an iTunes <c>Name</c> field into a piece title and an
/// ordered list of <see cref="ItunesImportInference.ParsedSubpieceRef"/>s.
/// </summary>
public class ItunesImportInferenceTests
{
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
