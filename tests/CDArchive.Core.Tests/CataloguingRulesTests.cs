using CDArchive.Core.Services;

namespace CDArchive.Core.Tests;

public class CataloguingRulesTests
{
    [Theory]
    [InlineData("Symphony in B flat, Op. 20", "Symphony in B-flat, Op. 20")]
    [InlineData("Concerto in F sharp minor", "Concerto in F-sharp minor")]
    [InlineData("Sonata in E flat", "Sonata in E-flat")]
    [InlineData("Suite in c sharp", "Suite in c-sharp")]
    [InlineData("Trio in D flat, Op. 70", "Trio in D-flat, Op. 70")]
    [InlineData("Symphony in C", "Symphony in C")] // no flat/sharp, unchanged
    [InlineData("Don Juan, Op. 20", "Don Juan, Op. 20")] // no key, unchanged
    [InlineData("Concerto in A flat for Two Pianos", "Concerto in A-flat for Two Pianos")]
    public void HyphenateKeys_FormatsCorrectly(string input, string expected)
    {
        Assert.Equal(expected, CataloguingRules.HyphenateKeys(input));
    }

    [Theory]
    [InlineData("#3", 9, "#3")] // 9 or fewer: no padding
    [InlineData("#3", 10, "#03")] // 10+: two digits
    [InlineData("#3", 15, "#03")]
    [InlineData("#12", 15, "#12")]
    [InlineData("#3", 104, "#003")] // 100+: three digits
    [InlineData("#45", 104, "#045")]
    [InlineData("#104", 104, "#104")]
    [InlineData("#1", 9, "#1")] // exactly 9: no padding
    public void PadWorkNumber_PadsCorrectly(string input, int maxInCatalogue, string expected)
    {
        Assert.Equal(expected, CataloguingRules.PadWorkNumber($"Symphony {input}", maxInCatalogue)
            .Replace("Symphony ", ""));
    }

    [Fact]
    public void PadWorkNumber_InFullContext()
    {
        var result = CataloguingRules.PadWorkNumber("Symphony #3 in E-flat", 15);
        Assert.Equal("Symphony #03 in E-flat", result);
    }

    [Theory]
    [InlineData("1. Allegro", 5, "1. Allegro")] // 5 movements: no padding
    [InlineData("1. Allegro", 9, "1. Allegro")] // exactly 9: no padding
    [InlineData("1. Allegro", 10, "01. Allegro")] // 10+: two digits
    [InlineData("3. Scherzo", 12, "03. Scherzo")]
    [InlineData("12. Finale", 12, "12. Finale")]
    [InlineData("1. Nacht", 22, "01. Nacht")] // Alpine Symphony style (22 sections)
    [InlineData("22. Nacht", 22, "22. Nacht")]
    public void PadMovementNumber_PadsCorrectly(string input, int totalMovements, string expected)
    {
        Assert.Equal(expected, CataloguingRules.PadMovementNumber(input, totalMovements));
    }

    [Fact]
    public void FormatTrackName_WorkOnly()
    {
        var result = CataloguingRules.FormatTrackName("Le roi d'Ys - Overture");
        Assert.Equal("Le roi d'Ys - Overture", result);
    }

    [Fact]
    public void FormatTrackName_WithMovement()
    {
        var result = CataloguingRules.FormatTrackName("Symphony in B flat, Op. 20", "1. Lent - Allegro vivo");
        Assert.Equal("Symphony in B-flat, Op. 20 - 1. Lent - Allegro vivo", result);
    }

    [Fact]
    public void FormatTrackName_HyphenatesKeys()
    {
        var result = CataloguingRules.FormatTrackName("Concerto in E flat, Op. 73", "1. Allegro");
        Assert.Equal("Concerto in E-flat, Op. 73 - 1. Allegro", result);
    }

    [Theory]
    [InlineData("Chausson", "Ernest", 1855, 1899, "Chausson, Ernest (1855\u20131899)")]
    [InlineData("Lalo", "\u00c9douard", 1823, 1892, "Lalo, \u00c9douard (1823\u20131892)")]
    [InlineData("Barraud", "Henry", 1900, 1997, "Barraud, Henry (1900\u20131997)")]
    public void FormatComposer_WithDates(string last, string first, int birth, int death, string expected)
    {
        Assert.Equal(expected, CataloguingRules.FormatComposer(last, first, birth, death));
    }

    [Fact]
    public void FormatComposer_LivingComposer()
    {
        var result = CataloguingRules.FormatComposer("Adams", "John", 1947, null);
        Assert.Equal("Adams, John (1947\u2013)", result);
    }

    [Theory]
    [InlineData("Detroit Symphony Orchestra", "Paul Paray", "Detroit Symphony Orchestra, Paul Paray")]
    [InlineData("Berlin Philharmonic", "", "Berlin Philharmonic")]
    public void FormatArtist_FormatsCorrectly(string ensemble, string conductor, string expected)
    {
        Assert.Equal(expected, CataloguingRules.FormatArtist(ensemble, conductor));
    }

    [Theory]
    [InlineData(5, 1)]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(99, 2)]
    [InlineData(100, 3)]
    [InlineData(104, 3)]
    public void MovementDigits_ReturnsCorrectWidth(int total, int expectedDigits)
    {
        Assert.Equal(expectedDigits, CataloguingRules.MovementDigits(total));
    }

    [Theory]
    [InlineData(9, 1)]
    [InlineData(10, 2)]
    [InlineData(104, 3)]
    public void WorkNumberDigits_ReturnsCorrectWidth(int max, int expectedDigits)
    {
        Assert.Equal(expectedDigits, CataloguingRules.WorkNumberDigits(max));
    }
}
