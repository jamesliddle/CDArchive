using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 small-editors (slice 5 — final): <see cref="MarkerEditorViewModel"/>
/// owns the 5 marker fields (Kind enum + Value + BarNumber/Number as
/// strings + Description). Preserves the H31 mutate-in-place contract so
/// the marker's stable Id is never reassigned.
/// </summary>
public class MarkerEditorViewModelTests
{
    // ── Default state ────────────────────────────────────────────────────────

    [Fact]
    public void NewVm_DefaultsKindToTempo_AndEmptyStrings()
    {
        var vm = new MarkerEditorViewModel();

        Assert.Equal(MarkerKind.Tempo, vm.Kind);
        Assert.Equal("", vm.Value);
        Assert.Equal("", vm.BarNumber);
        Assert.Equal("", vm.Number);
        Assert.Equal("", vm.Description);
    }

    [Fact]
    public void KindOptions_ContainsAllMarkerKinds_NoRetiredFirstLine()
    {
        var vm = new MarkerEditorViewModel();

        // FirstLine was retired — 4 kinds remain.
        Assert.Equal(4, vm.KindOptions.Count);
        Assert.Contains(vm.KindOptions, o => o.Kind == MarkerKind.Tempo);
        Assert.Contains(vm.KindOptions, o => o.Kind == MarkerKind.RehearsalMark);
        Assert.Contains(vm.KindOptions, o => o.Kind == MarkerKind.BarNumber);
        Assert.Contains(vm.KindOptions, o => o.Kind == MarkerKind.Section);
    }

    [Theory]
    [InlineData(MarkerKind.Tempo,         "Tempo indication")]
    [InlineData(MarkerKind.RehearsalMark, "Rehearsal mark")]
    [InlineData(MarkerKind.BarNumber,     "Bar number")]
    [InlineData(MarkerKind.Section,       "Section label")]
    public void FormatKind_ReturnsFriendlyLabel(MarkerKind kind, string expected)
    {
        Assert.Equal(expected, MarkerEditorViewModel.FormatKind(kind));
    }

    // ── LoadFromMarker ───────────────────────────────────────────────────────

    [Fact]
    public void LoadFromMarker_PopulatesAllFields()
    {
        var marker = new MusicalMarker
        {
            Kind        = MarkerKind.RehearsalMark,
            Value       = "Mark A",
            BarNumber   = 47,
            Number      = 1,
            Description = "Trio entry",
        };

        var vm = new MarkerEditorViewModel();
        vm.LoadFromMarker(marker);

        Assert.Equal(MarkerKind.RehearsalMark, vm.Kind);
        Assert.Equal("Mark A",                 vm.Value);
        Assert.Equal("47",                     vm.BarNumber);
        Assert.Equal("1",                      vm.Number);
        Assert.Equal("Trio entry",             vm.Description);
    }

    [Fact]
    public void LoadFromMarker_NullOptionalFields_BecomeEmptyStrings()
    {
        var marker = new MusicalMarker { Kind = MarkerKind.Tempo };
        var vm = new MarkerEditorViewModel();
        vm.LoadFromMarker(marker);

        Assert.Equal("", vm.Value);
        Assert.Equal("", vm.BarNumber);
        Assert.Equal("", vm.Number);
        Assert.Equal("", vm.Description);
    }

    [Fact]
    public void LoadFromMarker_NullIntegers_RenderAsEmptyStrings()
    {
        var marker = new MusicalMarker { Kind = MarkerKind.Tempo, BarNumber = null, Number = null };
        var vm = new MarkerEditorViewModel();
        vm.LoadFromMarker(marker);

        Assert.Equal("", vm.BarNumber);
        Assert.Equal("", vm.Number);
    }

    // ── SaveToMarker ─────────────────────────────────────────────────────────

    [Fact]
    public void SaveToMarker_WritesAllFields()
    {
        var marker = new MusicalMarker();
        var vm = new MarkerEditorViewModel
        {
            Kind        = MarkerKind.Section,
            Value       = "Wenn mein Schatz Hochzeit macht",
            BarNumber   = "12",
            Number      = "3",
            Description = "Lied I",
        };

        vm.SaveToMarker(marker);

        Assert.Equal(MarkerKind.Section,                  marker.Kind);
        Assert.Equal("Wenn mein Schatz Hochzeit macht",   marker.Value);
        Assert.Equal(12,                                  marker.BarNumber);
        Assert.Equal(3,                                   marker.Number);
        Assert.Equal("Lied I",                            marker.Description);
    }

    [Fact]
    public void SaveToMarker_EmptyOptionalFields_NormaliseToNull()
    {
        var marker = new MusicalMarker
        {
            Value = "OLD", BarNumber = 99, Number = 99, Description = "OLD",
        };
        var vm = new MarkerEditorViewModel { Kind = MarkerKind.BarNumber };
        // All optional fields empty.

        vm.SaveToMarker(marker);

        Assert.Equal(MarkerKind.BarNumber, marker.Kind);
        Assert.Null(marker.Value);
        Assert.Null(marker.BarNumber);
        Assert.Null(marker.Number);
        Assert.Null(marker.Description);
    }

    [Theory]
    [InlineData("47",  47)]
    [InlineData(" 47 ",47)]
    [InlineData("",    null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]
    [InlineData("47.5", null)]
    public void SaveToMarker_BarNumber_ParsedToInt_OrNull(string input, int? expected)
    {
        var marker = new MusicalMarker();
        var vm = new MarkerEditorViewModel { BarNumber = input };

        vm.SaveToMarker(marker);

        Assert.Equal(expected, marker.BarNumber);
    }

    [Theory]
    [InlineData("5",   5)]
    [InlineData("",    null)]
    [InlineData("xyz", null)]
    public void SaveToMarker_Number_ParsedToInt_OrNull(string input, int? expected)
    {
        var marker = new MusicalMarker();
        var vm = new MarkerEditorViewModel { Number = input };

        vm.SaveToMarker(marker);

        Assert.Equal(expected, marker.Number);
    }

    [Fact]
    public void SaveToMarker_TrimsValueAndDescription()
    {
        var marker = new MusicalMarker();
        var vm = new MarkerEditorViewModel
        {
            Value       = "  Allegro  ",
            Description = "  Notes  ",
        };

        vm.SaveToMarker(marker);

        Assert.Equal("Allegro", marker.Value);
        Assert.Equal("Notes",   marker.Description);
    }

    [Fact]
    public void SaveToMarker_MutatesInPlace_PreservesStableId_H31_Regression()
    {
        // Critical H31 contract: marker.Id is allocated by SQLite on first
        // save and never changes. Album-track refs anchor on Id, so a save
        // that reassigns Id would invalidate every anchored track. The
        // editor's contract is "mutate in place"; the VM enforces it by
        // writing fields to the supplied instance rather than constructing
        // a fresh one.
        var marker = new MusicalMarker
        {
            Id          = 42,                      // stable Id from SQLite
            Kind        = MarkerKind.Tempo,
            Value       = "Allegro",
        };

        var vm = new MarkerEditorViewModel();
        vm.LoadFromMarker(marker);
        vm.Value = "Andante";
        vm.Kind  = MarkerKind.Section;

        vm.SaveToMarker(marker);

        Assert.Equal(42, marker.Id);                       // Id preserved
        Assert.Equal(MarkerKind.Section, marker.Kind);     // edits applied
        Assert.Equal("Andante",          marker.Value);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new MusicalMarker
        {
            Id          = 17,
            Kind        = MarkerKind.RehearsalMark,
            Value       = "B",
            BarNumber   = 100,
            Number      = 2,
            Description = "second exposition",
        };

        var vm = new MarkerEditorViewModel();
        vm.LoadFromMarker(original);

        var roundTripped = new MusicalMarker { Id = 17 };   // mutate-in-place
        vm.SaveToMarker(roundTripped);

        Assert.Equal(original.Kind,        roundTripped.Kind);
        Assert.Equal(original.Value,       roundTripped.Value);
        Assert.Equal(original.BarNumber,   roundTripped.BarNumber);
        Assert.Equal(original.Number,      roundTripped.Number);
        Assert.Equal(original.Description, roundTripped.Description);
        Assert.Equal(17,                   roundTripped.Id);   // not touched
    }

    [Fact]
    public void KindOption_Record_HasExpectedShape()
    {
        var option = new KindOption(MarkerKind.Tempo, "Tempo indication");
        Assert.Equal(MarkerKind.Tempo, option.Kind);
        Assert.Equal("Tempo indication", option.Label);
    }
}
