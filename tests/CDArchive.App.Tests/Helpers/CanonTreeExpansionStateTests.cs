using CDArchive.App.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="CanonTreeExpansionState.ResolveSubpieceKey"/> — the
/// pure-logic predicate that maps a subpiece-area tree item to its
/// expansion-key. The class's Save/Restore methods themselves require a
/// realised WPF visual tree (<c>ItemContainerGenerator.ContainerFromItem</c>
/// only works after layout) so they're not unit-testable headlessly; smoke
/// testing of those is per the H2 slice 1 action item.
/// </summary>
public class CanonTreeExpansionStateTests
{
    [Fact]
    public void ResolveSubpieceKey_ForSubpieceDisplayNode_ReturnsTheUnderlyingPiece()
    {
        var piece = new CanonPiece { Title = "Allegro" };
        var node  = new SubpieceDisplayNode(piece);

        var key = CanonTreeExpansionState.ResolveSubpieceKey(node);

        // Key must be the piece instance itself so two different
        // SubpieceDisplayNode wrappers around the same piece collapse to one
        // expansion-state entry (which is what we want — the piece is what's
        // expanded, regardless of which wrapper realised the container).
        Assert.Same(piece, key);
    }

    [Fact]
    public void ResolveSubpieceKey_ForVersionDisplayNode_ReturnsTheUnderlyingVersion()
    {
        var version = new CanonPieceVersion { Description = "Orchestra" };
        var node    = new VersionDisplayNode(version);

        var key = CanonTreeExpansionState.ResolveSubpieceKey(node);

        Assert.Same(version, key);
    }

    [Fact]
    public void ResolveSubpieceKey_ForPieceOriginalNode_ReturnsValueTupleOfPieceAndOriginalSentinel()
    {
        var piece = new CanonPiece { Title = "Sonata #14" };
        var node  = new PieceOriginalNode(piece);

        var key = CanonTreeExpansionState.ResolveSubpieceKey(node);

        // Value-tuple struct equality is value-based, so two PieceOriginalNode
        // wrappers around the same piece produce equal keys — exactly what
        // we want for surviving a tree rebuild that constructs fresh wrappers.
        Assert.Equal((piece, "original"), key);
    }

    [Fact]
    public void ResolveSubpieceKey_TwoPieceOriginalNodes_AroundSamePiece_AreEqualKeys()
    {
        // Regression: the (Piece, "original") tuple is what makes the "Original"
        // node entry survive a tree rebuild — the rebuilt tree creates a fresh
        // PieceOriginalNode wrapper, but reference-identity on the wrapper would
        // miss. The struct tuple's value equality lets the HashSet hit.
        var piece = new CanonPiece { Title = "Sonata #14" };
        var nodeA = new PieceOriginalNode(piece);
        var nodeB = new PieceOriginalNode(piece);

        var keyA = CanonTreeExpansionState.ResolveSubpieceKey(nodeA);
        var keyB = CanonTreeExpansionState.ResolveSubpieceKey(nodeB);

        Assert.Equal(keyA, keyB);
        Assert.False(ReferenceEquals(nodeA, nodeB));
    }

    [Fact]
    public void ResolveSubpieceKey_ForUnrecognisedItem_ReturnsNull()
    {
        // Items the predicate doesn't recognise return null. The generic
        // TreeExpansionState helper treats null as "skip" — it neither records
        // nor restores the container. This is how we ignore separators,
        // headers, or any future item type we haven't taught the predicate
        // about.
        var key = CanonTreeExpansionState.ResolveSubpieceKey("not a node");

        Assert.Null(key);
    }

    [Fact]
    public void ResolveSubpieceKey_TwoSubpieceDisplayNodes_AroundSamePiece_ProduceSameKey()
    {
        // Regression: like the PieceOriginalNode case, but for SubpieceDisplayNode
        // — the key is the underlying piece's reference, not the wrapper's. The
        // HashSet uses ReferenceEqualityComparer.Instance for piece keys, so this
        // round-trips correctly across rebuilds that produce fresh wrappers.
        var piece = new CanonPiece { Title = "Andante" };
        var nodeA = new SubpieceDisplayNode(piece);
        var nodeB = new SubpieceDisplayNode(piece);

        var keyA = CanonTreeExpansionState.ResolveSubpieceKey(nodeA);
        var keyB = CanonTreeExpansionState.ResolveSubpieceKey(nodeB);

        Assert.Same(keyA, keyB);
    }
}
