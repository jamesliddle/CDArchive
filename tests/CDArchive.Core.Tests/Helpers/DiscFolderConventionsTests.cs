using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests.Helpers;

/// <summary>
/// Rework M35 + H46 regression: <see cref="DiscFolderConventions"/> is the
/// single source of truth for the "Disc N" folder naming convention.
/// Format / CandidateNames / IsDiscFolderName / SearchPattern all share
/// the same rules, so the four sites (scaffolding, locator, scanner,
/// cataloguer) can't drift independently and produce the kind of bug
/// H46 described (scaffolding writes "Disc 01" but locator looks for
/// "Disc 1").
/// </summary>
public class DiscFolderConventionsTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Format — used by AlbumScaffoldingService
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2,  "Disc 1")]
    [InlineData(2, 2,  "Disc 2")]
    [InlineData(9, 9,  "Disc 9")]
    public void Format_FewerThanTenDiscs_LeavesUnpadded(int discNumber, int totalDiscs, string expected)
    {
        Assert.Equal(expected, DiscFolderConventions.Format(discNumber, totalDiscs));
    }

    [Theory]
    [InlineData(1,  10, "Disc 01")]
    [InlineData(9,  10, "Disc 09")]
    [InlineData(10, 10, "Disc 10")]
    [InlineData(1,  12, "Disc 01")]
    [InlineData(12, 12, "Disc 12")]
    public void Format_TenOrMoreDiscs_PadsToTwoDigits(int discNumber, int totalDiscs, string expected)
    {
        Assert.Equal(expected, DiscFolderConventions.Format(discNumber, totalDiscs));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CandidateNames — the H46 fix: locator tries both forms
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandidateNames_DiscOne_YieldsBothUnpaddedAndPadded()
    {
        // Pre-fix the locator only tried "Disc 1" (unpadded) and missed the
        // "Disc 01" folder a 10+ disc scaffolding had created. CandidateNames
        // yields both so whichever exists wins.
        var candidates = DiscFolderConventions.CandidateNames(1).ToList();
        Assert.Equal(new[] { "Disc 1", "Disc 01" }, candidates);
    }

    [Theory]
    [InlineData(2,  "Disc 2",  "Disc 02")]
    [InlineData(9,  "Disc 9",  "Disc 09")]
    [InlineData(10, "Disc 10")]    // 10+ — only one form makes sense
    [InlineData(99, "Disc 99")]
    public void CandidateNames_YieldsExpectedForms(int discNumber, params string[] expected)
    {
        var candidates = DiscFolderConventions.CandidateNames(discNumber).ToList();
        Assert.Equal(expected, candidates);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IsDiscFolderName — used by ArchiveScannerService
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Disc 1",    true)]
    [InlineData("Disc 12",   true)]
    [InlineData("Disc 01",   true)]
    [InlineData("Disc 1-2",  true)]    // sub-disc form
    [InlineData("Disc",      false)]   // missing number
    [InlineData("Disc abc",  false)]   // non-numeric
    [InlineData("Disk 1",    false)]   // typo
    [InlineData("Track 1",   false)]   // unrelated folder
    [InlineData("",          false)]
    public void IsDiscFolderName_MatchesExpected(string name, bool expected)
    {
        Assert.Equal(expected, DiscFolderConventions.IsDiscFolderName(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SearchPattern — used by CataloguingService.FindMp3Folders
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SearchPattern_IsTheStandardDiscGlob()
    {
        // Pinned because the value is used as a glob by Directory.GetDirectories;
        // a careless rename would silently change which folders the cataloguer
        // walks.
        Assert.Equal("Disc *", DiscFolderConventions.SearchPattern);
    }
}
