using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 small-editors (slice 1): <see cref="ComposerEditorViewModel"/> owns
/// the 11 text fields + 2 string lists previously held as private state on
/// <c>ComposerEditorWindow</c>.
/// </summary>
public class ComposerEditorViewModelTests
{
    // ── LoadFromComposer ─────────────────────────────────────────────────────

    [Fact]
    public void LoadFromComposer_PopulatesAllScalarFields()
    {
        var composer = new CanonComposer
        {
            Name         = "Beethoven, Ludwig van",
            SortName     = "Beethoven",
            BirthDate    = "1770-12-17",
            BirthPlace   = "Bonn",
            BirthState   = "",
            BirthCountry = "Germany",
            DeathDate    = "1827-03-26",
            DeathPlace   = "Vienna",
            DeathState   = "",
            DeathCountry = "Austria",
            Notes        = "Late period",
        };

        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(composer);

        Assert.Equal("Beethoven, Ludwig van", vm.Name);
        Assert.Equal("Beethoven",             vm.SortName);
        Assert.Equal("1770-12-17",            vm.BirthDate);
        Assert.Equal("Bonn",                  vm.BirthPlace);
        Assert.Equal("Germany",               vm.BirthCountry);
        Assert.Equal("1827-03-26",            vm.DeathDate);
        Assert.Equal("Vienna",                vm.DeathPlace);
        Assert.Equal("Austria",               vm.DeathCountry);
        Assert.Equal("Late period",           vm.Notes);
    }

    [Fact]
    public void LoadFromComposer_NullFieldsBecomeEmptyStrings()
    {
        var composer = new CanonComposer { Name = "Has name", SortName = "Has-sort" };

        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(composer);

        Assert.Equal("Has name", vm.Name);
        Assert.Equal("Has-sort", vm.SortName);
        Assert.Equal("", vm.BirthDate);
        Assert.Equal("", vm.BirthPlace);
        Assert.Equal("", vm.BirthState);
        Assert.Equal("", vm.BirthCountry);
        Assert.Equal("", vm.DeathDate);
        Assert.Equal("", vm.DeathPlace);
        Assert.Equal("", vm.DeathState);
        Assert.Equal("", vm.DeathCountry);
        Assert.Equal("", vm.Notes);
    }

    [Fact]
    public void LoadFromComposer_PopulatesAliasesAndCatalogPrefixes()
    {
        var composer = new CanonComposer
        {
            Name = "X", SortName = "X",
            Aliases         = new List<string> { "Alias 1", "Alias 2" },
            CatalogPrefixes = new List<string> { "Op.", "WoO" },
        };

        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(composer);

        Assert.Equal(2, vm.Aliases.Count);
        Assert.Equal("Alias 1", vm.Aliases[0]);
        Assert.Equal("Alias 2", vm.Aliases[1]);
        Assert.Equal(2, vm.CatalogPrefixes.Count);
        Assert.Equal("Op.", vm.CatalogPrefixes[0]);
        Assert.Equal("WoO", vm.CatalogPrefixes[1]);
    }

    [Fact]
    public void LoadFromComposer_NullListsLeaveCollectionsEmpty()
    {
        var composer = new CanonComposer { Name = "X", SortName = "X" };
        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(composer);

        Assert.Empty(vm.Aliases);
        Assert.Empty(vm.CatalogPrefixes);
    }

    [Fact]
    public void LoadFromComposer_ReHydrate_Replaces_NotAppends()
    {
        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(new CanonComposer
        {
            Name = "X", SortName = "X",
            Aliases = new List<string> { "Old" },
        });
        Assert.Single(vm.Aliases);

        vm.LoadFromComposer(new CanonComposer
        {
            Name = "Y", SortName = "Y",
            Aliases = new List<string> { "New1", "New2" },
        });

        Assert.Equal(2, vm.Aliases.Count);
        Assert.Equal("New1", vm.Aliases[0]);
        Assert.Equal("New2", vm.Aliases[1]);
    }

    // ── SaveToComposer ───────────────────────────────────────────────────────

    [Fact]
    public void SaveToComposer_ValidFields_WritesAllScalars()
    {
        var composer = new CanonComposer();
        var vm = new ComposerEditorViewModel
        {
            Name         = "  Mozart, Wolfgang Amadeus  ",   // trim
            SortName     = "  Mozart  ",
            BirthDate    = "1756-01-27",
            BirthPlace   = "Salzburg",
            BirthCountry = "Austria",
            DeathDate    = "1791-12-05",
            DeathPlace   = "Vienna",
            DeathCountry = "Austria",
            Notes        = "Notes here",
        };

        var error = vm.SaveToComposer(composer);

        Assert.Equal(ComposerEditorViewModel.SaveValidationError.None, error);
        Assert.Equal("Mozart, Wolfgang Amadeus", composer.Name);
        Assert.Equal("Mozart", composer.SortName);
        Assert.Equal("1756-01-27", composer.BirthDate);
        Assert.Equal("Salzburg",   composer.BirthPlace);
        Assert.Null(composer.BirthState);                    // empty → null
        Assert.Equal("Austria",    composer.BirthCountry);
        Assert.Equal("1791-12-05", composer.DeathDate);
        Assert.Equal("Vienna",     composer.DeathPlace);
        Assert.Null(composer.DeathState);
        Assert.Equal("Austria",    composer.DeathCountry);
        Assert.Equal("Notes here", composer.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveToComposer_MissingName_ReturnsValidationError_LeavesComposerUnmutated(string name)
    {
        var composer = new CanonComposer { Name = "original", SortName = "original-sort" };
        var vm = new ComposerEditorViewModel { Name = name, SortName = "Mozart" };

        var error = vm.SaveToComposer(composer);

        Assert.Equal(ComposerEditorViewModel.SaveValidationError.MissingName, error);
        Assert.Equal("original", composer.Name);            // unchanged
        Assert.Equal("original-sort", composer.SortName);   // unchanged
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveToComposer_MissingSortName_ReturnsValidationError_LeavesComposerUnmutated(string sortName)
    {
        var composer = new CanonComposer { Name = "original", SortName = "original-sort" };
        var vm = new ComposerEditorViewModel { Name = "Mozart", SortName = sortName };

        var error = vm.SaveToComposer(composer);

        Assert.Equal(ComposerEditorViewModel.SaveValidationError.MissingSortName, error);
        Assert.Equal("original", composer.Name);
        Assert.Equal("original-sort", composer.SortName);
    }

    [Fact]
    public void SaveToComposer_EmptyOptionalFields_NormaliseToNull()
    {
        var composer = new CanonComposer
        {
            BirthDate = "old", DeathDate = "old",
            BirthPlace = "old", Notes = "old",
        };
        var vm = new ComposerEditorViewModel
        {
            Name = "X", SortName = "X",
            // All optional fields empty.
        };

        vm.SaveToComposer(composer);

        Assert.Null(composer.BirthDate);
        Assert.Null(composer.BirthPlace);
        Assert.Null(composer.BirthState);
        Assert.Null(composer.BirthCountry);
        Assert.Null(composer.DeathDate);
        Assert.Null(composer.DeathPlace);
        Assert.Null(composer.DeathState);
        Assert.Null(composer.DeathCountry);
        Assert.Null(composer.Notes);
    }

    [Fact]
    public void SaveToComposer_WritesAliasesAndCatalogPrefixes()
    {
        var composer = new CanonComposer();
        var vm = new ComposerEditorViewModel { Name = "X", SortName = "X" };
        vm.Aliases.Add("A1");
        vm.Aliases.Add("A2");
        vm.CatalogPrefixes.Add("Op.");

        vm.SaveToComposer(composer);

        Assert.NotNull(composer.Aliases);
        Assert.Equal(2, composer.Aliases!.Count);
        Assert.Equal("A1", composer.Aliases[0]);
        Assert.NotNull(composer.CatalogPrefixes);
        Assert.Single(composer.CatalogPrefixes!);
    }

    [Fact]
    public void SaveToComposer_EmptyLists_WriteNull()
    {
        var composer = new CanonComposer
        {
            Aliases = new List<string> { "OLD" },
            CatalogPrefixes = new List<string> { "OLD" },
        };
        var vm = new ComposerEditorViewModel { Name = "X", SortName = "X" };

        vm.SaveToComposer(composer);

        Assert.Null(composer.Aliases);
        Assert.Null(composer.CatalogPrefixes);
    }

    [Fact]
    public void SaveToComposer_IndependentListInstances_PostSaveMutationsDontAffectSaved()
    {
        var composer = new CanonComposer();
        var vm = new ComposerEditorViewModel { Name = "X", SortName = "X" };
        vm.Aliases.Add("First");

        vm.SaveToComposer(composer);
        Assert.Single(composer.Aliases!);

        vm.Aliases.Add("Late add");
        Assert.Single(composer.Aliases!);   // saved snapshot unchanged
    }

    [Fact]
    public void RoundTrip_AllFields_Idempotent()
    {
        var original = new CanonComposer
        {
            Name = "Beethoven, Ludwig van",
            SortName = "Beethoven",
            BirthDate = "1770-12-17",
            BirthPlace = "Bonn",
            BirthCountry = "Germany",
            DeathDate = "1827-03-26",
            DeathPlace = "Vienna",
            DeathCountry = "Austria",
            Notes = "Notes",
            Aliases = new List<string> { "LvB" },
            CatalogPrefixes = new List<string> { "Op.", "WoO" },
        };

        var vm = new ComposerEditorViewModel();
        vm.LoadFromComposer(original);

        var roundTripped = new CanonComposer();
        vm.SaveToComposer(roundTripped);

        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.SortName, roundTripped.SortName);
        Assert.Equal(original.BirthDate, roundTripped.BirthDate);
        Assert.Equal(original.BirthCountry, roundTripped.BirthCountry);
        Assert.Equal(original.DeathDate, roundTripped.DeathDate);
        Assert.Equal(original.Notes, roundTripped.Notes);
        Assert.Equal(original.Aliases, roundTripped.Aliases);
        Assert.Equal(original.CatalogPrefixes, roundTripped.CatalogPrefixes);
    }
}
