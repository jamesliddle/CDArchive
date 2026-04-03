using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests.Helpers;

public class StringSimilarityTests
{
    #region Normalize

    [Fact]
    public void Normalize_LowercasesInput()
    {
        var result = StringSimilarity.Normalize("HELLO WORLD");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Normalize_RemovesPunctuation()
    {
        var result = StringSimilarity.Normalize("Hello, World! (2024)");
        Assert.Equal("hello world 2024", result);
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        var result = StringSimilarity.Normalize("hello   world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Normalize_TrimsLeadingAndTrailingWhitespace()
    {
        var result = StringSimilarity.Normalize("  hello world  ");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Normalize_CombinesAllTransformations()
    {
        var result = StringSimilarity.Normalize("  The Beatles - Abbey Road (Remaster)  ");
        Assert.Equal("the beatles abbey road remaster", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_ReturnsEmptyForNullOrWhitespace(string? input)
    {
        var result = StringSimilarity.Normalize(input!);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Normalize_PreservesDigits()
    {
        var result = StringSimilarity.Normalize("Album 123");
        Assert.Equal("album 123", result);
    }

    #endregion

    #region LevenshteinDistance

    [Fact]
    public void LevenshteinDistance_IdenticalStrings_ReturnsZero()
    {
        var result = StringSimilarity.LevenshteinDistance("kitten", "kitten");
        Assert.Equal(0, result);
    }

    [Fact]
    public void LevenshteinDistance_EmptyVsNonEmpty_ReturnsLength()
    {
        Assert.Equal(5, StringSimilarity.LevenshteinDistance("", "hello"));
        Assert.Equal(5, StringSimilarity.LevenshteinDistance("hello", ""));
    }

    [Fact]
    public void LevenshteinDistance_BothEmpty_ReturnsZero()
    {
        Assert.Equal(0, StringSimilarity.LevenshteinDistance("", ""));
    }

    [Fact]
    public void LevenshteinDistance_KnownDistance_KittenSitting()
    {
        // kitten -> sitten -> sittin -> sitting = 3
        var result = StringSimilarity.LevenshteinDistance("kitten", "sitting");
        Assert.Equal(3, result);
    }

    [Fact]
    public void LevenshteinDistance_SingleCharacterDifference()
    {
        var result = StringSimilarity.LevenshteinDistance("cat", "bat");
        Assert.Equal(1, result);
    }

    [Fact]
    public void LevenshteinDistance_IsSymmetric()
    {
        var ab = StringSimilarity.LevenshteinDistance("abc", "xyz");
        var ba = StringSimilarity.LevenshteinDistance("xyz", "abc");
        Assert.Equal(ab, ba);
    }

    [Theory]
    [InlineData(null, null, 0)]
    [InlineData(null, "abc", 3)]
    [InlineData("abc", null, 3)]
    public void LevenshteinDistance_HandlesNullAsEmpty(string? a, string? b, int expected)
    {
        var result = StringSimilarity.LevenshteinDistance(a!, b!);
        Assert.Equal(expected, result);
    }

    #endregion
}
