using CDArchive.Core.Helpers;

namespace CDArchive.Core.Tests;

/// <summary>
/// Tests for <see cref="CatalogPrefixResolver"/> — the piece editor's Catalogue
/// dropdown source. Regression: a prefix added to a composer (e.g. "Anh." on
/// Beethoven) but not present in the global pick list used to be dropped because
/// the editor intersected the two lists.
/// </summary>
public class CatalogPrefixResolverTests
{
    private static readonly IReadOnlyList<string> Global =
        new[] { "B.", "BWV", "D.", "Op.", "WoO", "KV" };

    [Fact]
    public void ComposerWithPrefixes_ReturnsComposerListVerbatim_IncludingNonGlobal()
    {
        // Beethoven now has Anh. (not in the global list) — it must appear.
        var byComposer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Beethoven, Ludwig van"] = new[] { "Op.", "WoO", "Anh." },
        };

        var result = CatalogPrefixResolver.Resolve("Beethoven, Ludwig van", byComposer, Global);

        Assert.Equal(new[] { "Op.", "WoO", "Anh." }, result);
    }

    [Fact]
    public void ComposerListOrder_IsPreserved_NotReorderedByGlobal()
    {
        // Composer prefers WoO before Op.; the result must honour that order
        // rather than the global ordering (Op. before WoO).
        var byComposer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X"] = new[] { "WoO", "Op." },
        };

        Assert.Equal(new[] { "WoO", "Op." }, CatalogPrefixResolver.Resolve("X", byComposer, Global));
    }

    [Fact]
    public void ComposerNameTrimmed_BeforeLookup()
    {
        var byComposer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["Beethoven, Ludwig van"] = new[] { "Op.", "Anh." },
        };

        Assert.Equal(new[] { "Op.", "Anh." },
            CatalogPrefixResolver.Resolve("  Beethoven, Ludwig van  ", byComposer, Global));
    }

    [Fact]
    public void NoComposerEntry_FallsBackToGlobal()
    {
        var byComposer = new Dictionary<string, IReadOnlyList<string>>();
        Assert.Same(Global, CatalogPrefixResolver.Resolve("Unknown", byComposer, Global));
    }

    [Fact]
    public void EmptyComposerPrefixList_FallsBackToGlobal()
    {
        var byComposer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X"] = Array.Empty<string>(),
        };
        Assert.Same(Global, CatalogPrefixResolver.Resolve("X", byComposer, Global));
    }

    [Fact]
    public void NullComposerCatalogs_FallsBackToGlobal()
    {
        Assert.Same(Global, CatalogPrefixResolver.Resolve("X", null, Global));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankComposerName_FallsBackToGlobal(string? name)
    {
        var byComposer = new Dictionary<string, IReadOnlyList<string>>
        {
            ["X"] = new[] { "Op." },
        };
        Assert.Same(Global, CatalogPrefixResolver.Resolve(name, byComposer, Global));
    }
}
