using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Locks in H41's diagnostic surface: when two pieces under the same
/// composer share a normalized title key, the resolver's
/// <see cref="PieceReferenceIndex.Collisions"/> list records the
/// collision so the seeder + future diagnostics UI can surface it.
/// Pre-fix the second registrant was silently dropped via <c>TryAdd</c>.
/// </summary>
public class PieceReferenceIndexCollisionTests
{
    [Fact]
    public void Rebuild_NoCollisions_LeavesCollisionsListEmpty()
    {
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Beethoven, Ludwig van", Title = "Piano Concerto No. 1" },
            new() { Composer = "Beethoven, Ludwig van", Title = "Piano Concerto No. 2" },
            new() { Composer = "Mozart, Wolfgang Amadeus", Title = "Piano Concerto No. 21" },
        };

        var index = new PieceReferenceIndex(registerAsCurrent: false);
        index.BuildResolver(pieces);

        Assert.Empty(index.Collisions);
    }

    [Fact]
    public void Rebuild_TwoApprovedPiecesSharingTitleKey_RecordsCollision_AndDropsSecondRegistrant()
    {
        // Same composer, same Title, both approved. The first to be
        // registered wins the slot; the second is silently unreachable
        // via that title key.
        var keeper = new CanonPiece
        {
            Composer = "Schubert, Franz", Title = "Impromptu",
            IsProvisional = false,
        };
        var dropped = new CanonPiece
        {
            Composer = "Schubert, Franz", Title = "Impromptu",
            IsProvisional = false,
        };
        var pieces = new List<CanonPiece> { keeper, dropped };

        var index = new PieceReferenceIndex(registerAsCurrent: false);
        index.BuildResolver(pieces);

        // Exactly one collision per shared title key. The piece has only
        // one title variant (no DisplayTitle/Short distinction because
        // there's no catalog / number / key), so we expect one record.
        var c = Assert.Single(index.Collisions);
        Assert.Equal("Schubert, Franz", c.Composer);
        // NormalizeTitle preserves case; the dictionary uses OrdinalIgnoreCase
        // for lookup. So the recorded key is the original case.
        Assert.Equal("Impromptu", c.NormalizedKey);
        // First registrant wins (insertion order); the OrderBy IsProvisional
        // in RebuildInternal is stable so equal-priority pieces keep input order.
        Assert.Same(keeper, c.KeptPiece);
        Assert.Same(dropped, c.DroppedPiece);
    }

    [Fact]
    public void Rebuild_ApprovedAndProvisionalCollide_KeepsApproved()
    {
        // Even if the provisional piece appears first in the input,
        // RebuildInternal's `OrderBy(p => p.IsProvisional)` (false < true)
        // promotes the approved piece to register first.
        var provisional = new CanonPiece
        {
            Composer = "Brahms, Johannes", Title = "Intermezzo",
            IsProvisional = true,
        };
        var approved = new CanonPiece
        {
            Composer = "Brahms, Johannes", Title = "Intermezzo",
            IsProvisional = false,
        };

        var index = new PieceReferenceIndex(registerAsCurrent: false);
        // Provisional listed first — sort order should still promote approved.
        index.BuildResolver(new List<CanonPiece> { provisional, approved });

        var c = Assert.Single(index.Collisions);
        Assert.Same(approved, c.KeptPiece);
        Assert.Same(provisional, c.DroppedPiece);

        // Sanity: resolve the title and confirm the approved one wins.
        var resolved = index.TryResolve(new TrackPieceRef
        {
            Composer = "Brahms, Johannes",
            PieceTitle = "Intermezzo",
        });
        Assert.NotNull(resolved);
        Assert.Same(approved, resolved!.Value.Piece);
    }

    [Fact]
    public void Rebuild_DifferentComposers_SameTitle_NoCollision()
    {
        // The collision key is (composer, title) — same title under
        // different composers is fine.
        var pieces = new List<CanonPiece>
        {
            new() { Composer = "Bach, Johann Sebastian", Title = "Prelude in C" },
            new() { Composer = "Chopin, Frédéric", Title = "Prelude in C" },
        };

        var index = new PieceReferenceIndex(registerAsCurrent: false);
        index.BuildResolver(pieces);

        Assert.Empty(index.Collisions);
    }

    [Fact]
    public void Rebuild_TitleVariantsThatHappenToShareOneKey_RecordCollisionOnlyForSharedKey()
    {
        // EnumerateTitleKeys emits up to 5 variants per piece (Title,
        // DisplayTitle, DisplayTitleShort, stripped versions). Two pieces
        // can collide on one variant while differing on others. The
        // collision list should reflect the per-key granularity, not
        // declare a single "piece-vs-piece" collision.
        var pieceA = new CanonPiece
        {
            Composer = "Liszt, Franz", Title = "Hungarian Rhapsody",
            Number   = 2,
            IsProvisional = false,
        };
        var pieceB = new CanonPiece
        {
            // Same plain Title; different Number means DisplayTitle differs.
            Composer = "Liszt, Franz", Title = "Hungarian Rhapsody",
            Number   = 6,
            IsProvisional = false,
        };

        var index = new PieceReferenceIndex(registerAsCurrent: false);
        index.BuildResolver(new List<CanonPiece> { pieceA, pieceB });

        // At least one collision (the plain Title key collides).
        Assert.NotEmpty(index.Collisions);
        Assert.All(index.Collisions, c =>
        {
            Assert.Same(pieceA, c.KeptPiece);
            Assert.Same(pieceB, c.DroppedPiece);
            Assert.Equal("Liszt, Franz", c.Composer);
        });
        // pieceA should still be reachable by its more specific DisplayTitle —
        // the collision is on the bare-Title key, not on every variant.
        var resolved = index.TryResolve(new TrackPieceRef
        {
            Composer = "Liszt, Franz",
            PieceTitle = "Hungarian Rhapsody",
        });
        Assert.NotNull(resolved);
        Assert.Same(pieceA, resolved!.Value.Piece);
    }

    [Fact]
    public void Rebuild_CollisionsListResetOnEachRebuild()
    {
        // First build: two pieces collide.
        var p1 = new CanonPiece { Composer = "X", Title = "Same", IsProvisional = false };
        var p2 = new CanonPiece { Composer = "X", Title = "Same", IsProvisional = false };
        var index = new PieceReferenceIndex(registerAsCurrent: false);
        index.BuildResolver(new List<CanonPiece> { p1, p2 });
        Assert.NotEmpty(index.Collisions);

        // Second build with no collisions: list resets to empty.
        index.BuildResolver(new List<CanonPiece>
        {
            new() { Composer = "X", Title = "Different A" },
            new() { Composer = "X", Title = "Different B" },
        });
        Assert.Empty(index.Collisions);
    }
}
