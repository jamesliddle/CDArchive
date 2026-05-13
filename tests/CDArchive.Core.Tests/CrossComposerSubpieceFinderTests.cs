using CDArchive.Core.Models;

namespace CDArchive.Core.Tests;

/// <summary>
/// Covers the cross-composer subpiece detection used to surface collaborative
/// works (e.g. <em>L'éventail de Jeanne</em>) under each contributing composer.
/// </summary>
public class CrossComposerSubpieceFinderTests
{
    /// <summary>
    /// Builds a minimal piece with the given title + composer.
    /// </summary>
    private static CanonPiece P(string title, string? composer = null,
                                 int? number = null, string? form = null,
                                 List<CanonPiece>? subs = null)
        => new()
        {
            Title       = title,
            Composer    = composer,
            Number      = number,
            Form        = form,
            Subpieces   = subs,
            // Default so BuildSubpieceTitle yields "N. Title" or just "Title".
            NumberedSubpieces = true,
            // Required for EffectiveSubpiecesNumbered to default true on
            // pieces without an InstrumentationCategory in the test fixture.
            InstrumentationCategory = "Orchestra",
        };

    [Fact]
    public void Find_NoSubpieces_ReturnsEmpty()
    {
        var result = CrossComposerSubpieceFinder.Find(
            new[] { P("Symphony", "Beethoven") });
        Assert.Empty(result);
    }

    [Fact]
    public void Find_AllSubpiecesShareParentComposer_ReturnsEmpty()
    {
        // A normal Beethoven sonata: 4 movements, all inherit the parent's composer.
        var sonata = P("Sonata", "Beethoven", subs:
        [
            P("Allegro",      number: 1),
            P("Adagio",       number: 2),
            P("Menuetto",     number: 3),
            P("Prestissimo",  number: 4),
        ]);

        var result = CrossComposerSubpieceFinder.Find(new[] { sonata });
        Assert.Empty(result);
    }

    [Fact]
    public void Find_DepthOneCrossCredit_BucketsBySubpieceComposer()
    {
        // L'éventail de Jeanne: parent composer "(Various)", each movement
        // declares its own composer.
        var leventail = P("L'éventail de Jeanne", "(Various)", subs:
        [
            P("Fanfare", "Ravel",     number: 1),
            P("Marche",  "Ferroud",   number: 2),
            P("Valse",   "Ibert",     number: 3),
        ]);

        var result = CrossComposerSubpieceFinder.Find(new[] { leventail });

        Assert.Equal(3, result.Count);
        Assert.True(result.ContainsKey("Ravel"));
        Assert.True(result.ContainsKey("Ferroud"));
        Assert.True(result.ContainsKey("Ibert"));

        var ravel = Assert.Single(result["Ravel"]);
        Assert.Same(leventail, ravel.TopPiece);
        Assert.Same(leventail.Subpieces![0], ravel.Subpiece);
        Assert.Equal(new[] { leventail }, ravel.AncestorPath);
        Assert.Equal("L'éventail de Jeanne - 1. Fanfare", ravel.DisplayTitle);
    }

    [Fact]
    public void Find_StopsDescendingAtCrossCreditRoot()
    {
        // Ravel's Fanfare has its own (hypothetical) inner subpiece — that
        // subpiece should NOT generate a separate Ravel cross-credit, because
        // descendants of a cross-credit root inherit the root's composer.
        var fanfare = P("Fanfare", "Ravel", number: 1, subs:
        [
            P("Inner Section A", number: 1),
            P("Inner Section B", number: 2),
        ]);
        var leventail = P("L'éventail de Jeanne", "(Various)", subs: [fanfare]);

        var result = CrossComposerSubpieceFinder.Find(new[] { leventail });

        // One Ravel cross-credit (the Fanfare), not three.
        var ravelEntries = Assert.Single(result);
        Assert.Equal("Ravel", ravelEntries.Key);
        var ravelNode = Assert.Single(ravelEntries.Value);
        Assert.Same(fanfare, ravelNode.Subpiece);
    }

    [Fact]
    public void Find_DeepCrossCredit_BuildsCompositeTitleAcrossAncestors()
    {
        // Hypothetical multi-act collaborative opera:
        //   Opera (composer "(Various)")
        //     Act 1 (no own composer → inherits)
        //       Scene 2 (no own composer → inherits)
        //         Aria (composer "Mozart") ← cross-credit root, depth 3
        var aria = P("Aria", "Mozart", number: 4);
        var scene2 = P("Scene 2", number: 2, subs: [aria]);
        var act1   = P("Act 1",   number: 1, subs: [scene2]);
        var opera  = P("Collaborative Opera", "(Various)", subs: [act1]);

        var result = CrossComposerSubpieceFinder.Find(new[] { opera });

        var mozart = Assert.Single(result);
        var node = Assert.Single(mozart.Value);
        Assert.Same(aria, node.Subpiece);
        Assert.Same(opera, node.TopPiece);
        // Ancestor path covers the chain from top piece down to the immediate parent.
        Assert.Equal(new[] { opera, act1, scene2 }, node.AncestorPath);
        // Composite title walks through every ancestor's subpiece-formatted name.
        Assert.Equal("Collaborative Opera - 1. Act 1 - 2. Scene 2 - 4. Aria", node.DisplayTitle);
    }

    [Fact]
    public void Find_MultipleTopPieces_AccumulatesAcrossThem()
    {
        var leventail = P("L'éventail de Jeanne", "(Various)", subs:
        [
            P("Fanfare", "Ravel", number: 1),
        ]);
        var pictures = P("Pictures Reorchestrated", "(Various)", subs:
        [
            P("Promenade", "Ravel", number: 1),
        ]);

        var result = CrossComposerSubpieceFinder.Find(new[] { leventail, pictures });

        var ravel = result["Ravel"];
        Assert.Equal(2, ravel.Count);
        Assert.Contains(ravel, n => n.TopPiece == leventail);
        Assert.Contains(ravel, n => n.TopPiece == pictures);
    }

    [Fact]
    public void Find_SubpieceCarriesSameComposerAsParent_DoesNotCreateNode()
    {
        // Edge case: a subpiece explicitly carries the parent's composer (not
        // inherited via null). This shouldn't fire a cross-credit because the
        // composer hasn't actually changed.
        var sonata = P("Sonata", "Beethoven", subs:
        [
            P("Allegro", composer: "Beethoven", number: 1),
        ]);

        var result = CrossComposerSubpieceFinder.Find(new[] { sonata });
        Assert.Empty(result);
    }

    [Fact]
    public void Find_TopPieceWithEmptyComposer_IsSkipped()
    {
        // Defensive: a top piece without a composer can't anchor cross-credit
        // detection because there's no baseline to differ from.
        var orphan = P("Orphan", composer: null, subs:
        [
            P("Movement", "Ravel", number: 1),
        ]);

        var result = CrossComposerSubpieceFinder.Find(new[] { orphan });
        Assert.Empty(result);
    }
}
