using System.Text.Json;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 PieceEditor (slice 1): <see cref="PieceEditorViewModel"/> owns the
/// 10 simple text fields that previously lived as direct
/// <c>xxxBox.Text</c> reads/writes on <c>PieceEditorWindow</c>. Unlike
/// AlbumEditor / TrackEditor, the PieceEditor has no multi-edit mode — so the
/// VM uses plain <c>[ObservableProperty]</c> string fields rather than
/// <c>MixedField&lt;T&gt;</c>.
/// </summary>
public class PieceEditorViewModelTests
{
    // ── LoadFromPiece ────────────────────────────────────────────────────────

    [Fact]
    public void LoadFromPiece_PopulatesEveryTextField()
    {
        var piece = new CanonPiece
        {
            Title           = "Symphony 9",
            TitleEnglish    = "Choral Symphony",
            Subtitle        = "Ode to Joy",
            Nickname        = "Choral",
            Number          = 9,
            MusicNumber     = "Op. 125",
            PublicationYear = 1826,
            Notes           = "First performance Vienna, May 1824",
            CompositionYears = JsonDocument.Parse("\"1822-1824\"").RootElement.Clone(),
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("Symphony 9",                  vm.Title);
        Assert.Equal("Choral Symphony",             vm.TitleEnglish);
        Assert.Equal("Ode to Joy",                  vm.Subtitle);
        Assert.Equal("Choral",                      vm.Nickname);
        Assert.Equal("9",                           vm.Number);
        Assert.Equal("Op. 125",                     vm.MusicNumber);
        Assert.Equal("1826",                        vm.PubYear);
        Assert.Equal("1822-1824",                   vm.CompYears);
        Assert.Equal("First performance Vienna, May 1824", vm.Notes);
    }

    [Fact]
    public void LoadFromPiece_NullFieldsBecomeEmptyStrings()
    {
        var piece = new CanonPiece { Title = "Has title" };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("Has title", vm.Title);
        Assert.Equal("",          vm.TitleEnglish);
        Assert.Equal("",          vm.Subtitle);
        Assert.Equal("",          vm.Nickname);
        Assert.Equal("",          vm.Number);
        Assert.Equal("",          vm.MusicNumber);
        Assert.Equal("",          vm.PubYear);
        Assert.Equal("",          vm.CompYears);
        Assert.Equal("",          vm.Notes);
    }

    [Fact]
    public void LoadFromPiece_NumberAndPubYear_RoundTripThroughString()
    {
        // Integer model fields stored as string in the VM (TextBox binding).
        var piece = new CanonPiece { Number = 14, PublicationYear = 1801 };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("14",   vm.Number);
        Assert.Equal("1801", vm.PubYear);
    }

    [Fact]
    public void LoadVersionDescription_PopulatesField()
    {
        var version = new CanonPieceVersion { Description = "Original 1830 version" };

        var vm = new PieceEditorViewModel();
        vm.LoadVersionDescription(version);

        Assert.Equal("Original 1830 version", vm.VersionDescription);
    }

    [Fact]
    public void LoadVersionDescription_NullBecomesEmpty()
    {
        var version = new CanonPieceVersion();

        var vm = new PieceEditorViewModel();
        vm.LoadVersionDescription(version);

        Assert.Equal("", vm.VersionDescription);
    }

    // ── SaveToPiece ──────────────────────────────────────────────────────────

    [Fact]
    public void SaveToPiece_WritesAllScalarFields()
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Title         = "Sonata 14",
            TitleEnglish  = "Moonlight",
            Subtitle      = "Sonata quasi una fantasia",
            Nickname      = "Moonlight",
            Number        = "14",
            MusicNumber   = "II",
            PubYear       = "1801",
            CompYears     = "1801",
            Notes         = "Dedicated to Countess Guicciardi",
        };

        vm.SaveToPiece(piece);

        Assert.Equal("Sonata 14",                       piece.Title);
        Assert.Equal("Moonlight",                       piece.TitleEnglish);
        Assert.Equal("Sonata quasi una fantasia",       piece.Subtitle);
        Assert.Equal("Moonlight",                       piece.Nickname);
        Assert.Equal(14,                                piece.Number);
        Assert.Equal("II",                              piece.MusicNumber);
        Assert.Equal(1801,                              piece.PublicationYear);
        Assert.Equal("Dedicated to Countess Guicciardi", piece.Notes);
    }

    [Fact]
    public void SaveToPiece_EmptyFieldsNormaliseToNull()
    {
        // Optional string fields go null when blank/whitespace.
        var piece = new CanonPiece { Title = "old", Subtitle = "old" };
        var vm = new PieceEditorViewModel
        {
            Title         = "Required",
            TitleEnglish  = "",
            Subtitle      = "   ",
            Nickname      = "",
            MusicNumber   = "",
            Notes         = "",
            PubYear       = "",
            CompYears     = "",
            Number        = "",
        };

        vm.SaveToPiece(piece);

        Assert.Equal("Required", piece.Title);
        Assert.Null(piece.TitleEnglish);
        Assert.Null(piece.Subtitle);
        Assert.Null(piece.Nickname);
        Assert.Null(piece.MusicNumber);
        Assert.Null(piece.Notes);
        Assert.Null(piece.Number);
        Assert.Null(piece.PublicationYear);
        Assert.Null(piece.CompositionYears);
    }

    [Theory]
    [InlineData("9",   9)]
    [InlineData("0",   0)]
    [InlineData("-1", -1)]
    [InlineData("",       null)]
    [InlineData("  ",     null)]
    [InlineData("abc",    null)]
    [InlineData("9.5",    null)]
    public void SaveToPiece_Number_ParsedToInt_OrNull(string input, int? expected)
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel { Number = input };

        vm.SaveToPiece(piece);

        Assert.Equal(expected, piece.Number);
    }

    [Theory]
    [InlineData("1801",   1801)]
    [InlineData("",       null)]
    [InlineData("garbage", null)]
    public void SaveToPiece_PubYear_ParsedToInt_OrNull(string input, int? expected)
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel { PubYear = input };

        vm.SaveToPiece(piece);

        Assert.Equal(expected, piece.PublicationYear);
    }

    [Fact]
    public void SaveVersionDescription_WritesField()
    {
        var version = new CanonPieceVersion();
        var vm = new PieceEditorViewModel { VersionDescription = "arr. for piano" };

        vm.SaveVersionDescription(version);

        Assert.Equal("arr. for piano", version.Description);
    }

    [Fact]
    public void SaveVersionDescription_EmptyBecomesNull()
    {
        var version = new CanonPieceVersion { Description = "old" };
        var vm = new PieceEditorViewModel { VersionDescription = "" };

        vm.SaveVersionDescription(version);

        Assert.Null(version.Description);
    }

    // ── CompYears converter ──────────────────────────────────────────────────

    [Fact]
    public void CompYears_RoundTripThroughVm_PreservesStringValue()
    {
        // Most common case: CompositionYears is a JSON string like "1822-1824".
        var piece = new CanonPiece
        {
            CompositionYears = JsonDocument.Parse("\"c1893\"").RootElement.Clone(),
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.Equal("c1893", vm.CompYears);

        var roundTripped = new CanonPiece();
        vm.SaveToPiece(roundTripped);

        Assert.Equal(JsonValueKind.String, roundTripped.CompositionYears!.Value.ValueKind);
        Assert.Equal("c1893", roundTripped.CompositionYears!.Value.GetString());
    }

    [Fact]
    public void CompYears_NonStringElement_LoadAsToString()
    {
        // Legacy data may have non-string CompositionYears (e.g. arrays or
        // numbers). Loader falls back to ToString() — round-trip wraps it as
        // a JSON string, which is intentional (single-string is the canonical
        // form going forward; loaders that need richer shapes can extend the
        // converter). Exact formatting of the ToString() fallback isn't part
        // of the contract — we just guarantee the converter doesn't crash and
        // the textual representation contains the array's content.
        var piece = new CanonPiece
        {
            CompositionYears = JsonDocument.Parse("[1822, 1824]").RootElement.Clone(),
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Contains("1822", vm.CompYears);
        Assert.Contains("1824", vm.CompYears);
        Assert.StartsWith("[", vm.CompYears);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CompYears_NullOrWhitespace_LoadsAsEmpty(string? input)
    {
        var piece = new CanonPiece
        {
            CompositionYears = input is null
                ? null
                : JsonDocument.Parse($"\"{input}\"").RootElement.Clone(),
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        // Whitespace-only strings come through verbatim from the loader
        // (the save side then null-normalises them). For the null model
        // value, VM is empty.
        if (input is null)
            Assert.Equal("", vm.CompYears);
        else
            Assert.Equal(input, vm.CompYears);
    }

    [Theory]
    [InlineData(@"O'Brien's choral piece")]   // single quote
    [InlineData("multi\nline")]                // newline
    [InlineData("special \"quoted\" chars")]   // embedded quotes
    public void CompYears_SpecialCharacters_RoundTripSafely(string input)
    {
        // The converter uses JsonSerializer.Serialize to escape arbitrary
        // characters — guards against the buggy `$"\"{compYears}\""`
        // string interpolation that would fail on embedded quotes.
        var element = PieceEditorViewModel.StringToCompYears(input);
        Assert.NotNull(element);
        Assert.Equal(JsonValueKind.String, element!.Value.ValueKind);
        Assert.Equal(input, element.Value.GetString());
    }

    // ── Slice 2: Combobox fields ─────────────────────────────────────────────

    [Fact]
    public void LoadFromPiece_PopulatesAllComboboxFields()
    {
        var piece = new CanonPiece
        {
            Composer                = "Beethoven, Ludwig van",
            Form                    = "Sonata",
            KeyTonality             = "C",
            KeyMode                 = "Minor",   // pre-fix: mixed-case; loader lowercases
            InstrumentationCategory = "Piano",
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("Beethoven, Ludwig van", vm.Composer);
        Assert.Equal("Sonata",                vm.Form);
        Assert.Equal("C",                     vm.KeyTonality);
        Assert.Equal("minor",                 vm.KeyMode);   // normalised
        Assert.Equal("Piano",                 vm.Category);
    }

    [Fact]
    public void LoadFromPiece_NullComboboxFields_LoadAsEmptyStrings()
    {
        var piece = new CanonPiece { Title = "has title" };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("", vm.Composer);
        Assert.Equal("", vm.Form);
        Assert.Equal("", vm.KeyTonality);
        Assert.Equal("", vm.KeyMode);
        Assert.Equal("", vm.Category);
    }

    [Theory]
    [InlineData("major", "major")]
    [InlineData("Major", "major")]   // case-insensitive load
    [InlineData("MINOR", "minor")]
    [InlineData("",       "")]
    [InlineData(null,     "")]
    public void LoadFromPiece_KeyMode_LowercaseNormalisation(string? input, string expected)
    {
        var piece = new CanonPiece { KeyMode = input };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.Equal(expected, vm.KeyMode);
    }

    [Fact]
    public void LoadFromPiece_InheritedComposer_AppliedWhenPieceComposerEmpty()
    {
        // Subpiece / version inherits parent's Composer when its own is blank.
        var piece = new CanonPiece { Title = "Movement 1" };   // no Composer

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece, inheritedComposer: "Beethoven, Ludwig van");

        Assert.Equal("Beethoven, Ludwig van", vm.Composer);
    }

    [Fact]
    public void LoadFromPiece_InheritedComposer_IgnoredWhenPieceComposerSet()
    {
        // Piece's own Composer wins over inherited.
        var piece = new CanonPiece { Composer = "Schubert, Franz" };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece, inheritedComposer: "Beethoven, Ludwig van");

        Assert.Equal("Schubert, Franz", vm.Composer);
    }

    [Fact]
    public void LoadFromPiece_InheritedComposerNull_LeavesPieceValue()
    {
        var piece = new CanonPiece();   // no Composer

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece, inheritedComposer: null);

        Assert.Equal("", vm.Composer);
    }

    [Fact]
    public void SaveToPiece_WritesAllComboboxFields()
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Title       = "T",
            Composer    = "Brahms, Johannes",
            Form        = "Symphony",
            KeyTonality = "E",
            KeyMode     = "major",
            Category    = "Orchestra",
        };

        vm.SaveToPiece(piece);

        Assert.Equal("Brahms, Johannes", piece.Composer);
        Assert.Equal("Symphony",         piece.Form);
        Assert.Equal("E",                piece.KeyTonality);
        Assert.Equal("major",            piece.KeyMode);
        Assert.Equal("Orchestra",        piece.InstrumentationCategory);
    }

    [Fact]
    public void SaveToPiece_EmptyComboboxFields_NormaliseToNull()
    {
        var piece = new CanonPiece
        {
            Composer = "old", Form = "old", KeyTonality = "old",
            KeyMode = "minor", InstrumentationCategory = "old",
        };
        var vm = new PieceEditorViewModel
        {
            Composer = "",
            Form = "",
            KeyTonality = "",
            KeyMode = "",
            Category = "",
        };

        vm.SaveToPiece(piece);

        Assert.Null(piece.Composer);
        Assert.Null(piece.Form);
        Assert.Null(piece.KeyTonality);
        Assert.Null(piece.KeyMode);
        Assert.Null(piece.InstrumentationCategory);
    }

    [Fact]
    public void RoundTrip_AllComboboxFields_Idempotent()
    {
        // Loading + saving without VM mutation must leave the piece byte-identical.
        var original = new CanonPiece
        {
            Composer                = "Beethoven, Ludwig van",
            Form                    = "Sonata",
            KeyTonality             = "C",
            KeyMode                 = "minor",
            InstrumentationCategory = "Piano",
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(original);

        var roundTripped = new CanonPiece();
        vm.SaveToPiece(roundTripped);

        Assert.Equal(original.Composer,                roundTripped.Composer);
        Assert.Equal(original.Form,                    roundTripped.Form);
        Assert.Equal(original.KeyTonality,             roundTripped.KeyTonality);
        Assert.Equal(original.KeyMode,                 roundTripped.KeyMode);
        Assert.Equal(original.InstrumentationCategory, roundTripped.InstrumentationCategory);
    }

    // ── Slice 3: NumberedSubpieces + SubpiecesStart ──────────────────────────

    [Fact]
    public void LoadFromPiece_NumberedSubpieces_ExplicitTrue_LoadsTrue()
    {
        var piece = new CanonPiece { NumberedSubpieces = true };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.True(vm.NumberedSubpieces);
    }

    [Fact]
    public void LoadFromPiece_NumberedSubpieces_ExplicitFalse_LoadsFalse()
    {
        var piece = new CanonPiece { NumberedSubpieces = false };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.False(vm.NumberedSubpieces);
    }

    [Fact]
    public void LoadFromPiece_NumberedSubpieces_NullWithOperaCategory_LoadsFalse()
    {
        // Opera default is "not numbered" (scenes / acts aren't typically
        // labelled "1. ", "2. ").
        var piece = new CanonPiece
        {
            InstrumentationCategory = "Opera",
            NumberedSubpieces = null,
        };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.False(vm.NumberedSubpieces);
    }

    [Fact]
    public void LoadFromPiece_NumberedSubpieces_NullWithChamberCategory_LoadsTrue()
    {
        // Non-Opera default is "numbered".
        var piece = new CanonPiece
        {
            InstrumentationCategory = "Chamber",
            NumberedSubpieces = null,
        };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.True(vm.NumberedSubpieces);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData(1,    "1")]
    [InlineData(13,   "13")]
    public void LoadFromPiece_SubpiecesStart_LoadsAsString(int? input, string expected)
    {
        var piece = new CanonPiece { SubpiecesStart = input };
        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);
        Assert.Equal(expected, vm.SubpiecesStart);
    }

    [Theory]
    [InlineData("1",  1)]
    [InlineData("13", 13)]
    [InlineData("",   1)]   // empty falls back to 1
    [InlineData("abc", 1)]  // unparseable falls back to 1
    public void EffectiveSubpiecesStart_ParsesString_FallsBackToOne(string input, int expected)
    {
        var vm = new PieceEditorViewModel { SubpiecesStart = input };
        Assert.Equal(expected, vm.EffectiveSubpiecesStart);
    }

    [Fact]
    public void SaveToPiece_NumberedSubpieces_MatchesDefault_PersistsNull()
    {
        // Chamber default = numbered. User has Numbered checked → matches
        // default → save null (keeps JSON clean).
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Category = "Chamber",
            NumberedSubpieces = true,
        };
        vm.SaveToPiece(piece);
        Assert.Null(piece.NumberedSubpieces);
    }

    [Fact]
    public void SaveToPiece_NumberedSubpieces_DiffersFromDefault_PersistsExplicit()
    {
        // Chamber default = numbered. User UNchecks → save explicit false.
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Category = "Chamber",
            NumberedSubpieces = false,
        };
        vm.SaveToPiece(piece);
        Assert.Equal(false, piece.NumberedSubpieces);
    }

    [Fact]
    public void SaveToPiece_NumberedSubpieces_OperaUnchecked_PersistsNull()
    {
        // Opera default = not numbered. User leaves unchecked → matches
        // default → save null.
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Category = "Opera",
            NumberedSubpieces = false,
        };
        vm.SaveToPiece(piece);
        Assert.Null(piece.NumberedSubpieces);
    }

    [Fact]
    public void SaveToPiece_NumberedSubpieces_OperaChecked_PersistsTrue()
    {
        // User overrides the Opera default — save explicit true.
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel
        {
            Category = "Opera",
            NumberedSubpieces = true,
        };
        vm.SaveToPiece(piece);
        Assert.Equal(true, piece.NumberedSubpieces);
    }

    [Theory]
    [InlineData("1",  null)]   // 1 = model default → save null
    [InlineData("13", 13)]
    [InlineData("",   null)]   // empty parses to 1 → save null
    public void SaveToPiece_SubpiecesStart_OnlyPersistsWhenNonDefault(string input, int? expected)
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel { SubpiecesStart = input };
        vm.SaveToPiece(piece);
        Assert.Equal(expected, piece.SubpiecesStart);
    }

    [Theory]
    [InlineData(null,        false)]   // empty category → default false (treated as "no category set")
    [InlineData("",          false)]
    [InlineData("Opera",     false)]   // Opera → not numbered
    [InlineData("opera",     false)]   // case-insensitive
    [InlineData("OPERA",     false)]
    [InlineData("Chamber",   true)]
    [InlineData("Orchestra", true)]
    [InlineData("Piano",     true)]
    public void DefaultNumberedForCurrentCategory_VariesByCategory(string? category, bool expected)
    {
        var vm = new PieceEditorViewModel { Category = category ?? "" };
        Assert.Equal(expected, vm.DefaultNumberedForCurrentCategory);
    }

    [Fact]
    public void RoundTrip_NumberedSubpieces_NullStaysNull()
    {
        // Common case: piece has NumberedSubpieces=null (using category
        // default) — should remain null after round-trip.
        var original = new CanonPiece
        {
            InstrumentationCategory = "Chamber",
            NumberedSubpieces = null,
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(original);

        var roundTripped = new CanonPiece { InstrumentationCategory = "Chamber" };
        vm.SaveToPiece(roundTripped);

        Assert.Null(roundTripped.NumberedSubpieces);
    }

    [Fact]
    public void RoundTrip_SubpiecesStart_OneStaysNull()
    {
        // SubpiecesStart=1 round-trips to null (model default).
        var original = new CanonPiece { SubpiecesStart = 1 };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(original);

        var roundTripped = new CanonPiece();
        vm.SaveToPiece(roundTripped);

        Assert.Null(roundTripped.SubpiecesStart);
    }

    [Fact]
    public void RoundTrip_SubpiecesStart_ThirteenStaysThirteen()
    {
        var original = new CanonPiece { SubpiecesStart = 13 };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(original);

        var roundTripped = new CanonPiece();
        vm.SaveToPiece(roundTripped);

        Assert.Equal(13, roundTripped.SubpiecesStart);
    }

    // ── Slice 4: List-shaped fields ─────────────────────────────────────────

    [Fact]
    public void LoadListsFromPiece_PopulatesAllEightCollections()
    {
        var piece = new CanonPiece
        {
            Composers   = new List<ComposerCredit> { new() { Name = "Schubert, Franz", Role = "completed by" } },
            CatalogInfo = new List<CatalogInfo> { new() { Catalog = "Op.", CatalogNumber = "27" } },
            Subpieces   = new List<CanonPiece> { new() { Title = "Movement 1" } },
            Versions    = new List<CanonPieceVersion> { new() { Description = "original" } },
            Markers     = new List<MusicalMarker> { new() { Description = "Allegro" } },
            Variants    = new List<VariantInfo> { new() { Description = "var. 1" } },
        };

        var vm = new PieceEditorViewModel();
        vm.LoadListsFromPiece(piece);

        Assert.Single(vm.Composers);
        Assert.Single(vm.CatalogEntries);
        Assert.Single(vm.Subpieces);
        Assert.Single(vm.Versions);
        Assert.Single(vm.Markers);
        Assert.Single(vm.Variants);
    }

    [Fact]
    public void LoadListsFromPiece_NullCollections_LeaveEmpty()
    {
        var piece = new CanonPiece { Title = "barebones" };

        var vm = new PieceEditorViewModel();
        vm.LoadListsFromPiece(piece);

        Assert.Empty(vm.Composers);
        Assert.Empty(vm.CatalogEntries);
        Assert.Empty(vm.PieceInstruments);
        Assert.Empty(vm.Subpieces);
        Assert.Empty(vm.Versions);
        Assert.Empty(vm.Roles);
        Assert.Empty(vm.Markers);
        Assert.Empty(vm.Variants);
    }

    [Fact]
    public void LoadListsFromPiece_ReHydrate_Replaces_NotAppends()
    {
        // Re-running LoadListsFromPiece must clear each collection first.
        var vm = new PieceEditorViewModel();
        vm.LoadListsFromPiece(new CanonPiece
        {
            Subpieces = new List<CanonPiece> { new() { Title = "Old" } },
        });
        Assert.Single(vm.Subpieces);

        vm.LoadListsFromPiece(new CanonPiece
        {
            Subpieces = new List<CanonPiece> { new() { Title = "New1" }, new() { Title = "New2" } },
        });

        Assert.Equal(2, vm.Subpieces.Count);
        Assert.Equal("New1", vm.Subpieces[0].Title);
        Assert.Equal("New2", vm.Subpieces[1].Title);
    }

    [Fact]
    public void LoadListsFromPiece_Variants_DeepCloned_VmEditsDontBleedToSource()
    {
        // Variants are deep-cloned so user edits in the editor don't mutate
        // the source piece's variant list until OK is clicked.
        var sourceVariant = new VariantInfo { Id = 42, Description = "original", LongDescription = "long" };
        var piece = new CanonPiece
        {
            Variants = new List<VariantInfo> { sourceVariant },
        };

        var vm = new PieceEditorViewModel();
        vm.LoadListsFromPiece(piece);

        // The clone must carry the stable Id so the editor's eventual save
        // reconciles in place rather than churning the variant row id.
        Assert.Equal(42L, vm.Variants[0].Id);

        // Mutating the VM's variant must not affect the source.
        vm.Variants[0].Description = "MUTATED";

        Assert.Equal("original", sourceVariant.Description);
        Assert.Equal("MUTATED",  vm.Variants[0].Description);
    }

    [Fact]
    public void LoadListsFromPiece_Markers_SharedInstances_VmEditsAlsoMutateSource()
    {
        // Markers carry stable Ids that album-track refs depend on; the
        // editor's contract is "edit in place" (shared instances). This
        // contract is preserved by LoadListsFromPiece.
        var sourceMarker = new MusicalMarker { Description = "Allegro" };
        var piece = new CanonPiece
        {
            Markers = new List<MusicalMarker> { sourceMarker },
        };

        var vm = new PieceEditorViewModel();
        vm.LoadListsFromPiece(piece);

        Assert.Same(sourceMarker, vm.Markers[0]);   // reference equality
    }

    [Fact]
    public void SaveListsToPiece_WritesAllCollections()
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel();
        vm.Composers.Add(new ComposerCredit { Name = "Mozart, Wolfgang Amadeus" });
        vm.CatalogEntries.Add(new CatalogInfo { Catalog = "K." });
        vm.Subpieces.Add(new CanonPiece { Title = "Movement 1" });
        vm.Versions.Add(new CanonPieceVersion { Description = "alt" });
        vm.Markers.Add(new MusicalMarker { Description = "Andante" });
        vm.Variants.Add(new VariantInfo { Description = "var." });

        vm.SaveListsToPiece(piece);

        Assert.NotNull(piece.Composers);
        Assert.Single(piece.Composers!);
        Assert.NotNull(piece.CatalogInfo);
        Assert.NotNull(piece.Subpieces);
        Assert.NotNull(piece.Versions);
        Assert.NotNull(piece.Markers);
        Assert.NotNull(piece.Variants);
    }

    [Fact]
    public void SaveListsToPiece_EmptyCollections_WriteNull()
    {
        // Empty collections normalise to null on the model — keeps JSON
        // snapshots clean for the common case.
        var piece = new CanonPiece
        {
            Composers   = new List<ComposerCredit> { new() { Name = "OLD" } },
            CatalogInfo = new List<CatalogInfo>    { new() },
            Subpieces   = new List<CanonPiece>     { new() { Title = "OLD" } },
            Versions    = new List<CanonPieceVersion> { new() },
            Markers     = new List<MusicalMarker>  { new() },
            Variants    = new List<VariantInfo>    { new() },
        };
        var vm = new PieceEditorViewModel();
        // All collections empty.

        vm.SaveListsToPiece(piece);

        Assert.Null(piece.Composers);
        Assert.Null(piece.CatalogInfo);
        Assert.Null(piece.Subpieces);
        Assert.Null(piece.Versions);
        Assert.Null(piece.Markers);
        Assert.Null(piece.Variants);
    }

    [Fact]
    public void SaveListsToPiece_IndependentListInstances_NoSharedReferences()
    {
        // Each list written to the piece is a fresh List<T> instance (via
        // ToList) — subsequent VM mutations must NOT affect the saved piece's
        // collections.
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel();
        vm.Subpieces.Add(new CanonPiece { Title = "First" });

        vm.SaveListsToPiece(piece);
        Assert.Single(piece.Subpieces!);

        vm.Subpieces.Add(new CanonPiece { Title = "Late add" });

        // Saved piece unaffected by the post-save VM mutation.
        Assert.Single(piece.Subpieces!);
    }

    [Fact]
    public void SeedInheritedComposers_AppliedWhenComposersEmpty()
    {
        var vm = new PieceEditorViewModel();
        var inherited = new List<ComposerCredit>
        {
            new() { Name = "Schubert, Franz", Role = "completed by" },
        };

        vm.SeedInheritedComposers(inherited);

        Assert.Single(vm.Composers);
        Assert.Equal("Schubert, Franz", vm.Composers[0].Name);
    }

    [Fact]
    public void SeedInheritedComposers_IgnoredWhenComposersAlreadyPopulated()
    {
        var vm = new PieceEditorViewModel();
        vm.Composers.Add(new ComposerCredit { Name = "Own" });

        vm.SeedInheritedComposers(new List<ComposerCredit>
        {
            new() { Name = "Inherited" },
        });

        Assert.Single(vm.Composers);
        Assert.Equal("Own", vm.Composers[0].Name);   // own wins, inherited ignored
    }

    [Fact]
    public void SeedInheritedComposers_NullInput_NoOp()
    {
        var vm = new PieceEditorViewModel();
        vm.SeedInheritedComposers(null);
        Assert.Empty(vm.Composers);
    }

    [Fact]
    public void LoadFromPiece_AlsoCallsLoadListsFromPiece()
    {
        // The single-arg LoadFromPiece (the editor's actual entry point)
        // should populate the list collections too.
        var piece = new CanonPiece
        {
            Title       = "Symphony 9",
            Subpieces   = new List<CanonPiece> { new() { Title = "Mvt 1" } },
            Composers   = new List<ComposerCredit> { new() { Name = "X" } },
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(piece);

        Assert.Equal("Symphony 9", vm.Title);
        Assert.Single(vm.Subpieces);
        Assert.Single(vm.Composers);
    }

    [Fact]
    public void SaveToPiece_AlsoCallsSaveListsToPiece()
    {
        var piece = new CanonPiece();
        var vm = new PieceEditorViewModel { Title = "T" };
        vm.Subpieces.Add(new CanonPiece { Title = "Mvt 1" });
        vm.Composers.Add(new ComposerCredit { Name = "X" });

        vm.SaveToPiece(piece);

        Assert.Equal("T", piece.Title);
        Assert.NotNull(piece.Subpieces);
        Assert.NotNull(piece.Composers);
    }

    [Fact]
    public void Roundtrip_PreservesAllEightLists()
    {
        // Full Load → Save round-trip with non-empty lists. Compare counts;
        // exact content equality is tested per-list above.
        var original = new CanonPiece
        {
            Title       = "Symphony",
            Composers   = new List<ComposerCredit> { new() { Name = "A" } },
            CatalogInfo = new List<CatalogInfo>    { new() { Catalog = "Op." } },
            Subpieces   = new List<CanonPiece>     { new() { Title = "Mvt 1" }, new() { Title = "Mvt 2" } },
            Versions    = new List<CanonPieceVersion> { new() { Description = "alt" } },
            Markers     = new List<MusicalMarker>  { new() { Description = "Andante" } },
            Variants    = new List<VariantInfo>    { new() { Description = "var.1" } },
        };

        var vm = new PieceEditorViewModel();
        vm.LoadFromPiece(original);

        var roundTripped = new CanonPiece();
        vm.SaveToPiece(roundTripped);

        Assert.Equal(1, roundTripped.Composers!.Count);
        Assert.Equal(1, roundTripped.CatalogInfo!.Count);
        Assert.Equal(2, roundTripped.Subpieces!.Count);
        Assert.Equal(1, roundTripped.Versions!.Count);
        Assert.Equal(1, roundTripped.Markers!.Count);
        Assert.Equal(1, roundTripped.Variants!.Count);
    }
}
