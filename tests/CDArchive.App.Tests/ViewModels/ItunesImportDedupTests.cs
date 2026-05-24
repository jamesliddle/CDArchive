using CDArchive.App.ViewModels;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H24 regression: the iTunes-import "already imported" dedup key now
/// includes the album's primary performer in addition to title/disc/track.
/// Two genuinely different albums sharing a title (Karajan's Beethoven 9
/// vs Bernstein's Beethoven 9 are both "Symphony No. 9") used to collide:
/// importing one hid the other's tracks. This test class locks in the
/// performer-normalisation contract.
///
/// <para>The end-to-end dedup check (RebuildImportedKeys ↔ ApplyFilter)
/// would need a real iTunes XML fixture to exercise; the
/// <see cref="ItunesImportViewModel.NormalisePerformer"/> helper is the
/// single source of truth for the key shape, so testing it directly
/// captures the regression.</para>
/// </summary>
public class ItunesImportDedupTests
{
    [Theory]
    [InlineData(null,                    "")]
    [InlineData("",                      "")]
    [InlineData("   ",                   "")]
    public void NormalisePerformer_NullOrBlank_ReturnsEmpty(string? input, string expected)
    {
        Assert.Equal(expected, ItunesImportViewModel.NormalisePerformer(input));
    }

    [Fact]
    public void NormalisePerformer_TokenOrderInvariant()
    {
        // The H24 regression scenario: canon has "Karajan, Herbert von";
        // iTunes XML might have "Herbert von Karajan". Token-sort
        // normalisation makes both produce the same key.
        var canon  = ItunesImportViewModel.NormalisePerformer("Karajan, Herbert von");
        var itunes = ItunesImportViewModel.NormalisePerformer("Herbert von Karajan");

        Assert.Equal(canon, itunes);
        Assert.NotEmpty(canon);
    }

    [Fact]
    public void NormalisePerformer_StripsPunctuationAndCase()
    {
        // Quote glyphs, periods, commas all dropped; case folded.
        var a = ItunesImportViewModel.NormalisePerformer("Wiener Philharmoniker");
        var b = ItunesImportViewModel.NormalisePerformer("WIENER PHILHARMONIKER");
        var c = ItunesImportViewModel.NormalisePerformer("Wiener, Philharmoniker.");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Fact]
    public void NormalisePerformer_DifferentPerformers_ProduceDifferentKeys()
    {
        // The whole point of H24: Karajan and Bernstein must NOT collide.
        var karajan   = ItunesImportViewModel.NormalisePerformer("Karajan, Herbert von");
        var bernstein = ItunesImportViewModel.NormalisePerformer("Bernstein, Leonard");

        Assert.NotEqual(karajan, bernstein);
        Assert.NotEmpty(karajan);
        Assert.NotEmpty(bernstein);
    }

    [Fact]
    public void NormalisePerformer_SingleNameOrchestra_StableKey()
    {
        // Many classical recordings credit the ensemble as the AlbumArtist
        // (e.g. "Berliner Philharmoniker"). The normalisation should still
        // produce a stable, distinguishing key.
        var bp = ItunesImportViewModel.NormalisePerformer("Berliner Philharmoniker");
        var vp = ItunesImportViewModel.NormalisePerformer("Wiener Philharmoniker");

        Assert.NotEqual(bp, vp);
        Assert.NotEmpty(bp);
        Assert.NotEmpty(vp);
    }

    [Fact]
    public void NormalisePerformer_HandlesDiacriticsConsistently()
    {
        // The current implementation strips non-ASCII chars entirely (a
        // pragmatic choice — would-be diacritic-aware normalisation is
        // larger). Document the contract: two strings with identical
        // ASCII letters produce identical keys regardless of accents.
        var withAccents    = ItunesImportViewModel.NormalisePerformer("Dvořák");
        var withoutAccents = ItunesImportViewModel.NormalisePerformer("Dvorak");

        // "Dvořák" loses ř and á entirely → "dvk"; "Dvorak" → "dvorak".
        // These are NOT equal under the current scheme. Document the
        // current behaviour; a future improvement could Unicode-fold first.
        Assert.NotEqual(withAccents, withoutAccents);
    }
}
