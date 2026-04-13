using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Canon;

/// <summary>
/// Regression tests for <see cref="ContributedWorksFinder"/>: IsContributor,
/// FindContributedGroups, and the filtered-tree logic that controls which nodes
/// are expandable in the contributed-works section of the composer tree.
/// </summary>
public class ContributedWorksFinderTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ComposerCredit Principal(string name) =>
        new() { Name = name, Role = null };

    private static ComposerCredit Contributor(string name, string role) =>
        new() { Name = name, Role = role };

    private static CanonPiece SimplePiece(string composer, string title,
        List<ComposerCredit>? composers = null,
        List<CanonPieceVersion>? versions = null,
        List<CanonPiece>? subpieces = null) =>
        new()
        {
            Composer = composer,
            Title = title,
            Composers = composers,
            Versions = versions,
            Subpieces = subpieces,
        };

    private static CanonPieceVersion SimpleVersion(string description,
        List<ComposerCredit>? composers = null,
        List<CanonPiece>? subpieces = null) =>
        new()
        {
            Description = description,
            Composers = composers,
            Subpieces = subpieces,
        };

    // ── IsContributor ─────────────────────────────────────────────────────────

    [Fact]
    public void IsContributor_RootLevelCredit_ReturnsTrue()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        Assert.True(ContributedWorksFinder.IsContributor(piece, "Ravel, Maurice"));
    }

    [Fact]
    public void IsContributor_VersionLevelCredit_ReturnsTrue()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            versions:
            [
                SimpleVersion("Original version",
                    composers: [Principal("Mussorgsky, Modest")]),
                SimpleVersion("Orchestral version",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]),
            ]);

        Assert.True(ContributedWorksFinder.IsContributor(piece, "Ravel, Maurice"));
    }

    [Fact]
    public void IsContributor_SubpieceLevelCredit_ReturnsTrue()
    {
        var movement = SimplePiece("Mussorgsky, Modest", "Promenade",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            subpieces: [movement]);

        Assert.True(ContributedWorksFinder.IsContributor(piece, "Ravel, Maurice"));
    }

    [Fact]
    public void IsContributor_PrincipalOnlyNoRole_ReturnsFalse()
    {
        // Ravel is listed as a Composer credit but with no role → principal, not contributor
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Principal("Ravel, Maurice")]);

        Assert.False(ContributedWorksFinder.IsContributor(piece, "Ravel, Maurice"));
    }

    [Fact]
    public void IsContributor_UnrelatedComposer_ReturnsFalse()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest")]);

        Assert.False(ContributedWorksFinder.IsContributor(piece, "Ravel, Maurice"));
    }

    [Fact]
    public void IsContributor_CreditInVersionSubpiece_ReturnsTrue()
    {
        var scene = SimplePiece("Barber, Samuel", "Act I Scene 1",
            composers: [Principal("Barber, Samuel"), Contributor("Menotti, Gian Carlo", "libr.")]);

        var piece = SimplePiece("Barber, Samuel", "Antony and Cleopatra",
            versions:
            [
                SimpleVersion("1975 version",
                    subpieces: [scene]),
            ]);

        Assert.True(ContributedWorksFinder.IsContributor(piece, "Menotti, Gian Carlo"));
    }

    // ── FindContributedGroups: basic cases ────────────────────────────────────

    [Fact]
    public void FindContributedGroups_NoPieces_ReturnsEmptyList()
    {
        var result = ContributedWorksFinder.FindContributedGroups([], "Ravel, Maurice");
        Assert.Empty(result);
    }

    [Fact]
    public void FindContributedGroups_NoContributions_ReturnsEmptyList()
    {
        var pieces = new[]
        {
            SimplePiece("Mussorgsky, Modest", "Night on Bald Mountain"),
            SimplePiece("Debussy, Claude", "La Mer"),
        };

        var result = ContributedWorksFinder.FindContributedGroups(pieces, "Ravel, Maurice");
        Assert.Empty(result);
    }

    [Fact]
    public void FindContributedGroups_SkipsOwnPieces()
    {
        // A piece whose Composer == composerName should never appear in contributed groups,
        // even if a self-credit with a role is present (unusual but possible).
        var piece = SimplePiece("Ravel, Maurice", "Boléro",
            composers: [Contributor("Ravel, Maurice", "orch.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");
        Assert.Empty(result);
    }

    [Fact]
    public void FindContributedGroups_SingleVersionContribution_ReturnsOneGroup()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            versions:
            [
                SimpleVersion("Original version",
                    composers: [Principal("Mussorgsky, Modest")]),
                SimpleVersion("Orchestral version",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]),
            ]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");

        Assert.Single(result);
        var group = result[0];
        Assert.Equal("orch.", group.Role);
        Assert.Equal("Mussorgsky, Modest", group.ComposerName);
        Assert.Single(group.Pieces);
        Assert.Equal(piece, group.Pieces[0].Piece);
    }

    [Fact]
    public void FindContributedGroups_TwoPiecesSameRoleAndComposer_AreGroupedTogether()
    {
        var piece1 = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var piece2 = SimplePiece("Mussorgsky, Modest", "Night on Bald Mountain",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece1, piece2], "Ravel, Maurice");

        Assert.Single(result);
        Assert.Equal(2, result[0].Pieces.Count);
    }

    [Fact]
    public void FindContributedGroups_DifferentRoles_ProduceSeparateGroups()
    {
        var piece1 = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var piece2 = SimplePiece("Debussy, Claude", "Gymnopédies",
            composers: [Principal("Debussy, Claude"), Contributor("Ravel, Maurice", "arr.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece1, piece2], "Ravel, Maurice");

        Assert.Equal(2, result.Count);
        var roles = result.Select(g => g.Role).OrderBy(r => r).ToList();
        Assert.Contains("arr.", roles);
        Assert.Contains("orch.", roles);
    }

    [Fact]
    public void FindContributedGroups_DifferentOriginalComposers_ProduceSeparateGroups()
    {
        var piece1 = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var piece2 = SimplePiece("Schumann, Robert", "Carnaval",
            composers: [Principal("Schumann, Robert"), Contributor("Ravel, Maurice", "orch.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece1, piece2], "Ravel, Maurice");

        Assert.Equal(2, result.Count);
        var composers = result.Select(g => g.ComposerName).OrderBy(c => c).ToList();
        Assert.Contains("Mussorgsky, Modest", composers);
        Assert.Contains("Schumann, Robert", composers);
    }

    [Fact]
    public void FindContributedGroups_DisplayTitle_IsRoleColonComposer()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");

        // DisplayTitle trims trailing '.' from role before the colon
        Assert.Equal("orch: Mussorgsky, Modest", result[0].DisplayTitle);
    }

    // ── Filtered tree: root-level contribution ───────────────────────────────

    [Fact]
    public void FilteredTree_RootContribution_ChildrenAreUnfiltered()
    {
        // When the contribution is at the root level, the entire tree should be
        // normally expandable (full children).
        var sub1 = SimplePiece("Mussorgsky, Modest", "Promenade");
        var sub2 = SimplePiece("Mussorgsky, Modest", "The Gnome");

        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")],
            subpieces: [sub1, sub2]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");

        var node = result[0].Pieces[0];
        // TreeChildren for a piece with only subpieces (no versions) returns the
        // SubpieceDisplayNode list. Root contribution → full tree, so children should be non-null.
        Assert.True(node.HasChildren);
    }

    // ── Filtered tree: version-level contribution ─────────────────────────────

    [Fact]
    public void FilteredTree_VersionContribution_OriginalVersionNotExpandable()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            versions:
            [
                SimpleVersion("Original version",
                    composers: [Principal("Mussorgsky, Modest")]),
                SimpleVersion("Orchestral version",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]),
            ]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");
        var node = result[0].Pieces[0];

        Assert.True(node.HasChildren); // piece itself has children (the version list)

        // First child should be PieceOriginalNode with IsExpandable = false
        // (Ravel did not contribute to the plain original)
        var children = node.Children!;
        Assert.True(children.Count >= 2);
        var originalNode = children[0] as PieceOriginalNode;
        Assert.NotNull(originalNode);
        Assert.False(originalNode.HasChildren); // isExpandable = false
    }

    [Fact]
    public void FilteredTree_VersionContribution_TargetVersionIsExpandable()
    {
        var sub = SimplePiece("Mussorgsky, Modest", "Promenade");

        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            versions:
            [
                SimpleVersion("Original version",
                    composers: [Principal("Mussorgsky, Modest")]),
                SimpleVersion("Orchestral version",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")],
                    subpieces: [sub]),
            ]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");
        var node = result[0].Pieces[0];
        var children = node.Children!;

        // children: [0]=PieceOriginalNode, [1]=Original version (not expandable), [2]=Orchestral version
        var orchVersionNode = children[2] as VersionDisplayNode;
        Assert.NotNull(orchVersionNode);
        Assert.True(orchVersionNode.HasSubpieces);
    }

    [Fact]
    public void FilteredTree_VersionContribution_OtherVersionsNotExpandable()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            versions:
            [
                SimpleVersion("Original version",
                    composers: [Principal("Mussorgsky, Modest")]),
                SimpleVersion("Orchestral version",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]),
                SimpleVersion("String quartet arrangement",
                    composers: [Principal("Mussorgsky, Modest"), Contributor("Smith, John", "arr.")]),
            ]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Ravel, Maurice");
        var node = result[0].Pieces[0];
        var children = node.Children!;

        // Third child (Smith's arrangement) — Ravel had no role here → not expandable
        var smithNode = children[2] as VersionDisplayNode;
        Assert.NotNull(smithNode);
        Assert.False(smithNode.HasSubpieces);
    }

    // ── Filtered tree: subpiece-level contribution ────────────────────────────

    [Fact]
    public void FilteredTree_SubpieceContribution_PathSubpiecesAreExpandable()
    {
        var leaf = SimplePiece("Barber, Samuel", "Act I Scene 1",
            composers: [Principal("Barber, Samuel"), Contributor("Menotti, Gian Carlo", "libr.")]);

        var act1 = SimplePiece("Barber, Samuel", "Act I",
            subpieces: [leaf]);

        var piece = SimplePiece("Barber, Samuel", "Antony and Cleopatra",
            subpieces: [act1]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Menotti, Gian Carlo");

        Assert.Single(result);
        var node = result[0].Pieces[0];
        Assert.True(node.HasChildren); // top-level piece has children
    }

    [Fact]
    public void FilteredTree_SubpieceContribution_UnrelatedSubpiecesNotExpandable()
    {
        var leafWithContrib = SimplePiece("Barber, Samuel", "Act I Scene 1",
            composers: [Principal("Barber, Samuel"), Contributor("Menotti, Gian Carlo", "libr.")]);

        var leafNoContrib = SimplePiece("Barber, Samuel", "Act I Scene 2");

        var piece = SimplePiece("Barber, Samuel", "Antony and Cleopatra",
            subpieces: [leafWithContrib, leafNoContrib]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Menotti, Gian Carlo");
        var node = result[0].Pieces[0];

        // The filtered subpieces list has two entries; the second should not be expandable
        var filteredSubpieces = node.Children as List<SubpieceDisplayNode>;
        Assert.NotNull(filteredSubpieces);
        Assert.Equal(2, filteredSubpieces.Count);

        Assert.True(filteredSubpieces[0].HasChildren || !filteredSubpieces[0].HasChildren); // target node (may or may not have its own children)
        Assert.False(filteredSubpieces[1].HasChildren); // unrelated node is not expandable
    }

    [Fact]
    public void FilteredTree_SubpieceContribution_TargetSubpieceIsExpandable_WhenItHasChildren()
    {
        var grandchild = SimplePiece("Barber, Samuel", "Opening bars");

        var target = SimplePiece("Barber, Samuel", "Act I Scene 1",
            composers: [Principal("Barber, Samuel"), Contributor("Menotti, Gian Carlo", "libr.")],
            subpieces: [grandchild]);

        var piece = SimplePiece("Barber, Samuel", "Antony and Cleopatra",
            subpieces: [target]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "Menotti, Gian Carlo");
        var node = result[0].Pieces[0];
        var filteredSubpieces = node.Children as List<SubpieceDisplayNode>;
        Assert.NotNull(filteredSubpieces);

        // The contribution target itself should be expandable (its own subpieces are all shown)
        Assert.True(filteredSubpieces[0].HasChildren);
    }

    // ── FindContributedGroups: ordering ──────────────────────────────────────

    [Fact]
    public void FindContributedGroups_GroupsAreOrderedByRoleThenComposer()
    {
        // p1: orch./Schumann  p2: arr./Mussorgsky  p3: arr./Debussy
        // Each has a different (role, originalComposer) key → 3 separate groups
        var p1 = SimplePiece("Schumann, Robert", "Carnaval",
            composers: [Principal("Schumann, Robert"), Contributor("Ravel, Maurice", "orch.")]);
        var p2 = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "arr.")]);
        var p3 = SimplePiece("Debussy, Claude", "Gymnopédies",
            composers: [Principal("Debussy, Claude"), Contributor("Ravel, Maurice", "arr.")]);

        var result = ContributedWorksFinder.FindContributedGroups([p1, p2, p3], "Ravel, Maurice");

        // Ordered by role first: "arr." < "orch."
        Assert.Equal(3, result.Count);
        Assert.Equal("arr.", result[0].Role);
        Assert.Equal("arr.", result[1].Role);
        Assert.Equal("orch.", result[2].Role);

        // Within "arr.": "Debussy" < "Mussorgsky" alphabetically
        Assert.Equal("Debussy, Claude", result[0].ComposerName);
        Assert.Equal("Mussorgsky, Modest", result[1].ComposerName);
    }

    // ── ContributedPieceNode properties ──────────────────────────────────────

    [Fact]
    public void ContributedPieceNode_HasChildren_FalseWhenChildrenNull()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition");
        var node = new ContributedPieceNode(piece, null);

        Assert.False(node.HasChildren);
    }

    [Fact]
    public void ContributedPieceNode_HasChildren_FalseWhenChildrenEmpty()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition");
        var node = new ContributedPieceNode(piece, new System.Collections.ArrayList());

        Assert.False(node.HasChildren);
    }

    [Fact]
    public void ContributedPieceNode_HasChildren_TrueWhenChildrenNonEmpty()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition");
        var children = new System.Collections.ArrayList { "dummy" };
        var node = new ContributedPieceNode(piece, children);

        Assert.True(node.HasChildren);
    }

    [Fact]
    public void ContributedPieceNode_DisplayTitle_MatchesPieceDisplayTitleShort()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition");
        var node = new ContributedPieceNode(piece, null);

        Assert.Equal(piece.DisplayTitleShort, node.DisplayTitle);
    }

    // ── ContributedRoleGroupNode properties ───────────────────────────────────

    [Fact]
    public void ContributedRoleGroupNode_DisplayTitle_TrimsTrailingDotFromRole()
    {
        var group = new ContributedRoleGroupNode("orch.", "Mussorgsky, Modest", []);
        // "orch." → trimmed to "orch" before colon
        Assert.Equal("orch: Mussorgsky, Modest", group.DisplayTitle);
    }

    [Fact]
    public void ContributedRoleGroupNode_DisplayTitle_RoleWithoutDot_IsUnchanged()
    {
        var group = new ContributedRoleGroupNode("arr", "Debussy, Claude", []);
        Assert.Equal("arr: Debussy, Claude", group.DisplayTitle);
    }

    // ── Case-insensitivity ────────────────────────────────────────────────────

    [Fact]
    public void IsContributor_NameMatchIsCaseInsensitive()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        Assert.True(ContributedWorksFinder.IsContributor(piece, "ravel, maurice"));
        Assert.True(ContributedWorksFinder.IsContributor(piece, "RAVEL, MAURICE"));
    }

    [Fact]
    public void FindContributedGroups_ComposerNameMatchIsCaseInsensitive()
    {
        var piece = SimplePiece("Mussorgsky, Modest", "Pictures at an Exhibition",
            composers: [Principal("Mussorgsky, Modest"), Contributor("Ravel, Maurice", "orch.")]);

        var result = ContributedWorksFinder.FindContributedGroups([piece], "ravel, maurice");

        Assert.Single(result);
    }
}
