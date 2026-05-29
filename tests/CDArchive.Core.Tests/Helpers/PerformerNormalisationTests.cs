using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Tests for <see cref="PerformerNormalisation"/>. Anchors the contract for
/// both H24's "Hide already imported" filter and M5's iTunes album dedup —
/// the same key shape governs both.
/// </summary>
public class PerformerNormalisationTests
{
    // ── NormalisePerformer (single string) ────────────────────────────────

    [Theory]
    [InlineData(null,                       "")]
    [InlineData("",                         "")]
    [InlineData("   ",                      "")]
    [InlineData("Karajan",                  "karajan")]
    [InlineData("Karajan, Herbert von",     "herbertkarajanvon")]
    [InlineData("Herbert von Karajan",      "herbertkarajanvon")]
    [InlineData("HERBERT VON KARAJAN",      "herbertkarajanvon")]
    [InlineData("Herbert  von  Karajan",    "herbertkarajanvon")]   // collapsed whitespace
    [InlineData("Karajan, Herbert von, Berlin Philharmonic",
                "berlinherbertkarajanphilharmonicvon")]
    public void NormalisePerformer_VariousInputs(string? input, string expected)
    {
        Assert.Equal(expected, PerformerNormalisation.NormalisePerformer(input));
    }

    // ── NormaliseAlbumPerformerList (canon-side helper) ───────────────────

    [Fact]
    public void NormaliseAlbumPerformerList_NullList_ReturnsEmpty()
    {
        Assert.Equal("", PerformerNormalisation.NormaliseAlbumPerformerList(null));
    }

    [Fact]
    public void NormaliseAlbumPerformerList_EmptyList_ReturnsEmpty()
    {
        Assert.Equal("", PerformerNormalisation.NormaliseAlbumPerformerList(
            new List<AlbumPerformer>()));
    }

    [Fact]
    public void NormaliseAlbumPerformerList_SinglePerformer_MatchesSingleStringNormalisation()
    {
        // User stored the performer as a single "Last, First" entry — equivalent
        // to passing the same string through NormalisePerformer directly.
        var list = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } };
        Assert.Equal(
            PerformerNormalisation.NormalisePerformer("Karajan, Herbert von"),
            PerformerNormalisation.NormaliseAlbumPerformerList(list));
    }

    [Fact]
    public void NormaliseAlbumPerformerList_MultiSplitPerformers_MatchSingleStringKey_M5Regression()
    {
        // The headline M5 bug: iTunes Artist "Karajan, Herbert von" comma-splits
        // into two AlbumPerformer entries. The canon-side key MUST produce the
        // same output as the iTunes-side key (whole string through
        // NormalisePerformer). Joining all performer names back together
        // restores the symmetry.
        var canonSide = new List<AlbumPerformer>
        {
            new() { Name = "Karajan" },
            new() { Name = "Herbert von" },
        };
        var itunesSide = "Karajan, Herbert von";

        Assert.Equal(
            PerformerNormalisation.NormalisePerformer(itunesSide),
            PerformerNormalisation.NormaliseAlbumPerformerList(canonSide));
    }

    [Fact]
    public void NormaliseAlbumPerformerList_WithEmptyEntries_SkipsThem()
    {
        var list = new List<AlbumPerformer>
        {
            new() { Name = "Karajan" },
            new() { Name = "" },
            new() { Name = "Herbert von" },
            new() { Name = "  " },
        };
        Assert.Equal(
            PerformerNormalisation.NormalisePerformer("Karajan, Herbert von"),
            PerformerNormalisation.NormaliseAlbumPerformerList(list));
    }

    [Fact]
    public void NormaliseAlbumPerformerList_DifferentPerformers_ProducesDifferentKeys()
    {
        // Sanity check that the join doesn't collapse genuinely different
        // performers into the same key.
        var karajan   = new List<AlbumPerformer> { new() { Name = "Karajan, Herbert von" } };
        var bernstein = new List<AlbumPerformer> { new() { Name = "Bernstein, Leonard"   } };

        Assert.NotEqual(
            PerformerNormalisation.NormaliseAlbumPerformerList(karajan),
            PerformerNormalisation.NormaliseAlbumPerformerList(bernstein));
    }
}
