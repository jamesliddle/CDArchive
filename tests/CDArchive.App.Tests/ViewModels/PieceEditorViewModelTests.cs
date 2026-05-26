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
}
