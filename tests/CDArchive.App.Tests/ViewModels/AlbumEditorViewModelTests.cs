using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 (slice 1): <see cref="AlbumEditorViewModel"/> owns the 7 simple text
/// fields the album editor previously read/wrote via direct
/// <c>TitleBox.Text</c> manipulation. Each field is a
/// <see cref="Helpers.MixedField{T}"/> wrapper. This test class locks in the
/// Load contract — single-edit hydrates Unanimous from one album; multi-edit
/// detects per-field unanimity and falls back to Mixed when values differ.
/// </summary>
public class AlbumEditorViewModelTests
{
    [Fact]
    public void LoadSingle_PopulatesEveryTextField_FromAlbum()
    {
        var album = new CanonAlbum
        {
            Title           = "Symphony 9",
            Subtitle        = "Choral",
            Label           = "DG",
            CatalogueNumber = "447-401",
            Barcode         = "028944740127",
            ArchiveFolder   = "Beethoven Sym 9 Karajan",
            Notes           = "1962 cycle",
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(album);

        Assert.Equal("Symphony 9",                vm.Title.Value);
        Assert.Equal("Choral",                    vm.Subtitle.Value);
        Assert.Equal("DG",                        vm.Label.Value);
        Assert.Equal("447-401",                   vm.CatalogueNumber.Value);
        Assert.Equal("028944740127",              vm.Barcode.Value);
        Assert.Equal("Beethoven Sym 9 Karajan",   vm.ArchiveFolder.Value);
        Assert.Equal("1962 cycle",                vm.Notes.Value);

        // None of the fields are mixed; none have been edited (Init resets).
        Assert.False(vm.Title.IsMixed);
        Assert.False(vm.Title.WasEdited);
    }

    [Fact]
    public void LoadSingle_NullFieldsBecomeEmptyString()
    {
        // CanonAlbum's optional fields are nullable; the VM normalises to "".
        var album = new CanonAlbum { Title = "Has title" };

        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(album);

        Assert.Equal("Has title", vm.Title.Value);
        Assert.Equal("",          vm.Subtitle.Value);
        Assert.Equal("",          vm.Label.Value);
        Assert.Equal("",          vm.Notes.Value);
    }

    [Fact]
    public void LoadMulti_AllAlbumsAgreeOnField_LoadsUnanimous()
    {
        var albums = new[]
        {
            new CanonAlbum { Title = "A", Label = "DG" },
            new CanonAlbum { Title = "B", Label = "DG" },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, "(Mixed)");

        // Title differs → Mixed; Label agrees → Unanimous.
        Assert.True(vm.Title.IsMixed);
        Assert.Equal("(Mixed)", vm.Title.Value);

        Assert.False(vm.Label.IsMixed);
        Assert.Equal("DG", vm.Label.Value);
    }

    [Fact]
    public void LoadMulti_FieldsThatDiffer_LoadAsMixed_WithPlaceholder()
    {
        var albums = new[]
        {
            new CanonAlbum { Title = "A", Subtitle = "alpha" },
            new CanonAlbum { Title = "B", Subtitle = "beta"  },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, "PLACEHOLDER");

        Assert.True(vm.Title.IsMixed);
        Assert.Equal("PLACEHOLDER", vm.Title.Value);
        Assert.True(vm.Subtitle.IsMixed);
        Assert.Equal("PLACEHOLDER", vm.Subtitle.Value);
    }

    [Fact]
    public void LoadMulti_NullVsEmptyString_TreatedAsSame()
    {
        // Both null and "" normalise to "" → unanimous, not mixed. Without
        // this normalisation a multi-selection where one album has null Label
        // and another has "" Label would spuriously show as Mixed.
        var albums = new[]
        {
            new CanonAlbum { Title = "A", Label = null },
            new CanonAlbum { Title = "B", Label = "" },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, "(Mixed)");

        Assert.False(vm.Label.IsMixed);
        Assert.Equal("", vm.Label.Value);
    }

    [Fact]
    public void LoadMulti_SingleAlbumInSelection_LoadsAsUnanimous()
    {
        // Degenerate case — multi-edit with a 1-album selection. Every field
        // is trivially unanimous; no Mixed sentinel needed.
        var albums = new[] { new CanonAlbum { Title = "Only one" } };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, "(Mixed)");

        Assert.False(vm.Title.IsMixed);
        Assert.Equal("Only one", vm.Title.Value);
    }

    // ── Slice 2: SparsCode + IsStereo ────────────────────────────────────────

    [Theory]
    [InlineData(null,      "Unknown")]
    [InlineData("",        "Unknown")]
    [InlineData("DDD",     "DDD")]
    [InlineData("ADD",     "ADD")]
    [InlineData("AAD",     "AAD")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("DDA",     "DDA")]   // legacy non-standard code passes through verbatim
    public void LoadSingle_SparsCode_NullAndEmptyMapToUnknown(string? input, string expected)
    {
        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(new CanonAlbum { SparsCode = input });
        Assert.Equal(expected, vm.SparsCode.Value);
        Assert.False(vm.SparsCode.IsMixed);
    }

    [Theory]
    [InlineData(null,  "Unknown")]
    [InlineData(true,  "Stereo")]
    [InlineData(false, "Mono")]
    public void LoadSingle_IsStereo_ConvertsBoolNullableToStringVocabulary(bool? input, string expected)
    {
        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(new CanonAlbum { IsStereo = input });
        Assert.Equal(expected, vm.IsStereo.Value);
        Assert.False(vm.IsStereo.IsMixed);
    }

    [Fact]
    public void LoadMulti_DifferingSparsCodes_LoadAsMixedSentinel()
    {
        var albums = new[]
        {
            new CanonAlbum { SparsCode = "DDD" },
            new CanonAlbum { SparsCode = "ADD" },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, mixedPlaceholder: "TEXTMIXED");

        // SparsCode uses its OWN sentinel constant, not the text-field one,
        // so the comboboxes can append their distinct ComboBoxItem.
        Assert.True(vm.SparsCode.IsMixed);
        Assert.Equal(AlbumEditorViewModel.SparsCodeMixedSentinel, vm.SparsCode.Value);
    }

    [Fact]
    public void LoadMulti_DifferingIsStereo_LoadAsMixedSentinel()
    {
        var albums = new[]
        {
            new CanonAlbum { IsStereo = true },
            new CanonAlbum { IsStereo = false },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, mixedPlaceholder: "TEXTMIXED");

        Assert.True(vm.IsStereo.IsMixed);
        Assert.Equal(AlbumEditorViewModel.IsStereoMixedSentinel, vm.IsStereo.Value);
    }

    [Fact]
    public void LoadMulti_NullSparsCodeUnanimousWithEmpty_TreatedAsSame()
    {
        // Both null and "" map to "Unknown" under SparsCodeToString — so a
        // multi-edit where one album has null and another has "" SparsCode
        // shouldn't show as Mixed.
        var albums = new[]
        {
            new CanonAlbum { SparsCode = null },
            new CanonAlbum { SparsCode = ""   },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(albums, "(Mixed)");

        Assert.False(vm.SparsCode.IsMixed);
        Assert.Equal("Unknown", vm.SparsCode.Value);
    }

    [Theory]
    [InlineData("DDD",      "DDD")]
    [InlineData("Unknown",  "Unknown")]   // "Unknown" stores as the literal string (matches pre-fix
                                          // behaviour of SparsCodeCombo.GetValue returning the
                                          // ComboBoxItem Content as-is)
    [InlineData("",         null)]
    [InlineData(null,       null)]
    public void SparsCodeFromString_RoundTripBehaviour(string? input, string? expected)
    {
        Assert.Equal(expected, AlbumEditorViewModel.SparsCodeFromString(input));
    }

    [Theory]
    [InlineData("Stereo",     true)]
    [InlineData("Mono",       false)]
    [InlineData("Unknown",    null)]
    [InlineData("Mixed",      null)]   // sentinel maps to null too (defensive — the editor's
                                       // save path guards against writing when sentinel is selected,
                                       // but if it slips through, null is the safe default)
    [InlineData("",           null)]
    [InlineData(null,         null)]
    public void IsStereoFromString_RoundTripBehaviour(string? input, bool? expected)
    {
        Assert.Equal(expected, AlbumEditorViewModel.IsStereoFromString(input));
    }

    // ── Performers + session lists (ObservableCollections) ───────────────────

    [Fact]
    public void LoadSingle_PopulatesPerformersAndSessionListsFromAlbum()
    {
        var album = new CanonAlbum
        {
            Performers = new List<AlbumPerformer>
            {
                new() { Name = "Karajan, Herbert von", Role = "Conductor" },
                new() { Name = "Berliner Philharmoniker" },
            },
            SessionEngineers = new List<string> { "Karl-Heinz Schneider" },
            SessionProducers = new List<string> { "John Culshaw" },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(album);

        Assert.Equal(2, vm.Performers.Count);
        Assert.Equal("Karajan, Herbert von", vm.Performers[0].Name);
        Assert.Equal(new[] { "Karl-Heinz Schneider" }, vm.SessionEngineers);
        Assert.Equal(new[] { "John Culshaw" },          vm.SessionProducers);
    }

    [Fact]
    public void LoadSingle_NullPerformersAndSessionLists_LeaveCollectionsEmpty()
    {
        var album = new CanonAlbum { Title = "no lists" };

        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(album);

        Assert.Empty(vm.Performers);
        Assert.Empty(vm.SessionEngineers);
        Assert.Empty(vm.SessionProducers);
    }

    [Fact]
    public void LoadSingle_ReHydratesAlreadyPopulatedCollections()
    {
        // Re-loading the editor with a different album must clear the
        // previous lists, not append to them.
        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(new CanonAlbum
        {
            Performers = new List<AlbumPerformer> { new() { Name = "First" } },
        });
        Assert.Single(vm.Performers);

        vm.LoadSingle(new CanonAlbum
        {
            Performers = new List<AlbumPerformer> { new() { Name = "Second" } },
        });

        Assert.Single(vm.Performers);
        Assert.Equal("Second", vm.Performers[0].Name);
    }

    [Fact]
    public void LoadMulti_LeavesPerformersAndSessionListsEmpty()
    {
        // Performers + session-name-list sections are hidden in multi-edit
        // (H18 visibility); the VM collections should stay empty so a stale
        // single-edit hydration doesn't bleed into the multi-edit view.
        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(new CanonAlbum
        {
            Performers       = new List<AlbumPerformer> { new() { Name = "lingers?" } },
            SessionEngineers = new List<string> { "engineer" },
            SessionProducers = new List<string> { "producer" },
        });
        Assert.Single(vm.Performers);

        vm.LoadMulti(new[]
        {
            new CanonAlbum { Title = "A" },
            new CanonAlbum { Title = "B" },
        }, "(Mixed)");

        Assert.Empty(vm.Performers);
        Assert.Empty(vm.SessionEngineers);
        Assert.Empty(vm.SessionProducers);
    }
}
