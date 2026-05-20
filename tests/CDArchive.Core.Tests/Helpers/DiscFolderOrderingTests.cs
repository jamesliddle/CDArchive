using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Rework H29 regression: <see cref="DiscFolderOrdering"/> sorts disc
/// folders numerically so "Disc 10" lands AFTER "Disc 2" — pre-fix both
/// ArchiveScannerService and CataloguingService used lexicographic
/// <c>.OrderBy(d => d)</c>, silently scrambling the disc-number assignment
/// on any 10+ disc box set.
/// </summary>
public class DiscFolderOrderingTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // ParseDiscKey
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Disc 1",  1, 0)]
    [InlineData("Disc 2",  2, 0)]
    [InlineData("Disc 10", 10, 0)]
    [InlineData("Disc 99", 99, 0)]
    public void ParseDiscKey_PlainNumber_ReturnsPrimaryWithZeroSecondary(
        string name, int expectedPrimary, int expectedSecondary)
    {
        var (primary, secondary) = DiscFolderOrdering.ParseDiscKey(name);
        Assert.Equal(expectedPrimary, primary);
        Assert.Equal(expectedSecondary, secondary);
    }

    [Theory]
    [InlineData("Disc 01", 1)]
    [InlineData("Disc 02", 2)]
    [InlineData("Disc 09", 9)]
    public void ParseDiscKey_ZeroPadded_StillParsesNumerically(string name, int expectedPrimary)
    {
        Assert.Equal(expectedPrimary, DiscFolderOrdering.ParseDiscKey(name).Primary);
    }

    [Theory]
    [InlineData("Disc 1-2", 1, 2)]
    [InlineData("Disc 3-4", 3, 4)]
    [InlineData("Disc 10-11", 10, 11)]
    public void ParseDiscKey_SubDisc_PopulatesSecondary(
        string name, int expectedPrimary, int expectedSecondary)
    {
        var (primary, secondary) = DiscFolderOrdering.ParseDiscKey(name);
        Assert.Equal(expectedPrimary, primary);
        Assert.Equal(expectedSecondary, secondary);
    }

    [Theory]
    [InlineData("Not a disc")]
    [InlineData("Discs 1-3")]   // typo — extra 's' on Disc
    [InlineData("Disk 1")]      // wrong spelling
    [InlineData("Disc")]        // missing number
    [InlineData("Disc abc")]    // non-numeric
    public void ParseDiscKey_NonMatching_SortsToEndViaMaxValue(string name)
    {
        var (primary, secondary) = DiscFolderOrdering.ParseDiscKey(name);
        Assert.Equal(int.MaxValue, primary);
        Assert.Equal(int.MaxValue, secondary);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OrderByDiscNumber
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OrderByDiscNumber_PutsTenDiscsInNumericOrder()
    {
        // The H29 regression itself: lexicographic OrderBy(d => d) puts
        // "Disc 10" before "Disc 2" — the new helper preserves numeric
        // order across the 10-disc boundary.
        var paths = new[]
        {
            @"C:\Box\Disc 10",
            @"C:\Box\Disc 2",
            @"C:\Box\Disc 1",
            @"C:\Box\Disc 11",
            @"C:\Box\Disc 3",
        };

        var ordered = DiscFolderOrdering.OrderByDiscNumber(paths).ToList();

        Assert.Equal(@"C:\Box\Disc 1",  ordered[0]);
        Assert.Equal(@"C:\Box\Disc 2",  ordered[1]);
        Assert.Equal(@"C:\Box\Disc 3",  ordered[2]);
        Assert.Equal(@"C:\Box\Disc 10", ordered[3]);
        Assert.Equal(@"C:\Box\Disc 11", ordered[4]);
    }

    [Fact]
    public void OrderByDiscNumber_SubDiscIsSecondarySort()
    {
        var paths = new[]
        {
            @"C:\Box\Disc 1-2",
            @"C:\Box\Disc 1",
            @"C:\Box\Disc 1-1",
            @"C:\Box\Disc 2",
        };

        var ordered = DiscFolderOrdering.OrderByDiscNumber(paths).ToList();

        // "Disc 1" (sub=0) before "Disc 1-1" (sub=1) before "Disc 1-2"
        // (sub=2), then "Disc 2".
        Assert.Equal(@"C:\Box\Disc 1",   ordered[0]);
        Assert.Equal(@"C:\Box\Disc 1-1", ordered[1]);
        Assert.Equal(@"C:\Box\Disc 1-2", ordered[2]);
        Assert.Equal(@"C:\Box\Disc 2",   ordered[3]);
    }

    [Fact]
    public void OrderByDiscNumber_PaddedAndUnpaddedMixedInterleaveByValue()
    {
        // Sometimes users pad and sometimes don't. Numeric sort treats them
        // identically: "Disc 09" sorts the same as "Disc 9".
        var paths = new[]
        {
            @"C:\Box\Disc 10",
            @"C:\Box\Disc 09",
            @"C:\Box\Disc 1",
        };

        var ordered = DiscFolderOrdering.OrderByDiscNumber(paths).ToList();

        Assert.Equal(@"C:\Box\Disc 1",  ordered[0]);
        Assert.Equal(@"C:\Box\Disc 09", ordered[1]);
        Assert.Equal(@"C:\Box\Disc 10", ordered[2]);
    }

    [Fact]
    public void OrderByDiscNumber_NonMatchingSortsLast()
    {
        var paths = new[]
        {
            @"C:\Box\Other",
            @"C:\Box\Disc 2",
            @"C:\Box\Disc 1",
        };

        var ordered = DiscFolderOrdering.OrderByDiscNumber(paths).ToList();

        Assert.Equal(@"C:\Box\Disc 1",  ordered[0]);
        Assert.Equal(@"C:\Box\Disc 2",  ordered[1]);
        Assert.Equal(@"C:\Box\Other",   ordered[2]);
    }

    [Fact]
    public void OrderByDiscNumber_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DiscFolderOrdering.OrderByDiscNumber(Array.Empty<string>()).ToList());
    }
}
