using CDArchive.Core.Models;
using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

/// <summary>
/// Rework H7 regression: a throwaway <see cref="PieceReferenceIndex"/>
/// constructed for resolution-only use (in
/// <see cref="SqliteCanonDataService"/>'s save paths and
/// <c>ItunesImporter</c>) used to silently steal
/// <see cref="PieceReferenceIndex.Current"/> via its constructor, leaving the
/// static accessor pointing at an empty-hits index. Every
/// <c>HitCountBadgeConverter</c> read during the save window then returned 0
/// — badges flickered to zero mid-save and recovered on the next real
/// Rebuild.
///
/// <para>
/// The fix introduced an <c>internal PieceReferenceIndex(bool registerAsCurrent)</c>
/// overload. Callers that genuinely own the canonical state (App-side
/// Rebuild / RebuildContainers flow) still get the default ctor and continue
/// to register themselves as Current. Throwaway sites pass false and
/// <see cref="PieceReferenceIndex.Current"/> stays put.
/// </para>
/// </summary>
public class PieceReferenceIndexCurrentTests
{
    /// <summary>
    /// Sentinel value used to seed Current so the test can assert it survived
    /// the throwaway construction. Each test is responsible for setting this
    /// up via the default-ctor path.
    /// </summary>
    private static PieceReferenceIndex SetCurrentToFreshIndex()
    {
        // Default ctor sets Current = this.
        return new PieceReferenceIndex();
    }

    /// <summary>
    /// Happy-path check on the new ctor overload: passing
    /// <c>registerAsCurrent: false</c> means the throwaway does NOT take
    /// over Current.
    /// </summary>
    [Fact]
    public void Constructor_WithRegisterAsCurrentFalse_DoesNotChangeCurrent()
    {
        var live = SetCurrentToFreshIndex();
        Assert.Same(live, PieceReferenceIndex.Current);

        var throwaway = new PieceReferenceIndex(registerAsCurrent: false);

        Assert.Same(live, PieceReferenceIndex.Current);
        Assert.NotSame(throwaway, PieceReferenceIndex.Current);
    }

    /// <summary>
    /// The H7 regression itself. Default-ctor throwaways stole Current; the
    /// new ctor overload doesn't. Both shapes are asserted here so the
    /// default-ctor behaviour (preserved for backward compatibility) is
    /// locked in too.
    /// </summary>
    [Fact]
    public void DefaultConstructor_StillStealsCurrent_AsBeforeFix()
    {
        var live = SetCurrentToFreshIndex();
        Assert.Same(live, PieceReferenceIndex.Current);

        var stealer = new PieceReferenceIndex();   // default ctor — sets Current

        // This is the pre-fix behaviour, intentionally preserved for the
        // App's DI-registered singleton which legitimately wants to claim
        // Current. The throwaway sites are the ones we changed.
        Assert.Same(stealer, PieceReferenceIndex.Current);
    }

    /// <summary>
    /// End-to-end scenario: build a live index with real hit data, then
    /// simulate a save-path throwaway that calls BuildResolver. With
    /// registerAsCurrent:false, the live index's hit data is still
    /// reachable via PieceReferenceIndex.Current — exactly what
    /// HitCountBadgeConverter relies on during a save.
    /// </summary>
    [Fact]
    public void ThrowawayWithBuildResolver_LeavesLiveHitDataReachableViaCurrent()
    {
        var piece = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Symphony No. 9" };
        var albums = new List<CanonAlbum>
        {
            new()
            {
                Title = "Karajan / DG",
                Discs = new List<AlbumDisc>
                {
                    new()
                    {
                        DiscNumber = 1,
                        Tracks = new List<AlbumTrack>
                        {
                            new()
                            {
                                TrackNumber = 1,
                                PieceRefs = new List<TrackPieceRef>
                                {
                                    new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Symphony No. 9" },
                                },
                            },
                        },
                    },
                },
            },
        };

        var live = new PieceReferenceIndex();
        live.Rebuild(new[] { piece }, albums);
        Assert.Same(live, PieceReferenceIndex.Current);
        var hitsBefore = live.CountForPiece(piece);
        Assert.True(hitsBefore > 0, "Expected the live index to have at least one hit for the piece.");

        // Save-path simulation: throwaway with registerAsCurrent:false, then
        // BuildResolver. Pre-fix the throwaway's default ctor would have set
        // Current to itself; the hit dictionaries on the throwaway are
        // empty, so live.CountForPiece via Current would have returned 0.
        var throwaway = new PieceReferenceIndex(registerAsCurrent: false);
        throwaway.BuildResolver(new[] { piece });

        // Current is still the live index — converters reading
        // PieceReferenceIndex.Current.CountForPiece(piece) during the save
        // window see the real hit count.
        Assert.Same(live, PieceReferenceIndex.Current);
        Assert.Equal(hitsBefore, PieceReferenceIndex.Current!.CountForPiece(piece));
    }
}
