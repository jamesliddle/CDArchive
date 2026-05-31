using System.Text.Json;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 TrackEditor extraction (slice 1): <see cref="TrackEditorViewModel"/>
/// owns the 5 text fields (TrackNumber as string for binding, Duration,
/// Description, FlacPath, Mp3Path). This test class locks in the Load
/// contracts — LoadSingle from one track, LoadNew for the add-new case,
/// LoadMulti for multi-edit, LoadLoose for the loose-track ctor.
/// </summary>
public class TrackEditorViewModelTests
{
    // ── LoadSingle ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadSingle_PopulatesEveryTextField_FromTrack()
    {
        var track = new AlbumTrack
        {
            TrackNumber = 7,
            Duration    = "5:32",
            Description = "Interview with Brendel",
            FlacPath    = @"C:\Music\track7.flac",
            Mp3Path     = @"C:\Music\track7.mp3",
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track);

        Assert.Equal("7",                       vm.TrackNumber.Value);
        Assert.Equal("5:32",                    vm.Duration.Value);
        Assert.Equal("Interview with Brendel",  vm.Description.Value);
        Assert.Equal(@"C:\Music\track7.flac",   vm.FlacPath.Value);
        Assert.Equal(@"C:\Music\track7.mp3",    vm.Mp3Path.Value);

        // Unanimous; not edited.
        Assert.False(vm.TrackNumber.IsMixed);
        Assert.False(vm.TrackNumber.StartedMixed);
        Assert.False(vm.TrackNumber.WasEdited);
    }

    [Fact]
    public void LoadSingle_NullOptionalFields_BecomeEmptyString()
    {
        // AlbumTrack's optional string fields are nullable; the VM normalises to "".
        var track = new AlbumTrack { TrackNumber = 3 };   // duration/desc/paths null

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track);

        Assert.Equal("3", vm.TrackNumber.Value);
        Assert.Equal("",  vm.Duration.Value);
        Assert.Equal("",  vm.Description.Value);
        Assert.Equal("",  vm.FlacPath.Value);
        Assert.Equal("",  vm.Mp3Path.Value);
    }

    // ── LoadNew (add-new track) ──────────────────────────────────────────────

    [Fact]
    public void LoadNew_EmptyDisc_StartsTrackNumberAtOne()
    {
        var disc = new AlbumDisc { DiscNumber = 1 };
        var vm = new TrackEditorViewModel();
        vm.LoadNew(disc);

        Assert.Equal("1", vm.TrackNumber.Value);
        Assert.Equal("",  vm.Duration.Value);
        Assert.Equal("",  vm.Description.Value);
    }

    [Fact]
    public void LoadNew_PopulatedDisc_PicksMaxPlusOne()
    {
        var disc = new AlbumDisc
        {
            DiscNumber = 1,
            Tracks =
            {
                new() { TrackNumber = 1 },
                new() { TrackNumber = 5 },   // not contiguous
                new() { TrackNumber = 3 },
            },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadNew(disc);

        Assert.Equal("6", vm.TrackNumber.Value);
    }

    // ── LoadMulti ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadMulti_UnanimousTextFields_LoadAsUnanimous()
    {
        // All tracks share Duration = "3:00" → unanimous.
        // TrackNumbers differ → Mixed.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Duration = "3:00" },
            new AlbumTrack { TrackNumber = 2, Duration = "3:00" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.True(vm.TrackNumber.IsMixed);
        Assert.True(vm.TrackNumber.StartedMixed);
        Assert.Equal("(Mixed)", vm.TrackNumber.Value);

        Assert.False(vm.Duration.IsMixed);
        Assert.False(vm.Duration.StartedMixed);
        Assert.Equal("3:00", vm.Duration.Value);
    }

    [Fact]
    public void LoadMulti_DifferingDescription_LoadsAsMixed()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Description = "Intro" },
            new AlbumTrack { TrackNumber = 2, Description = "Outro" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "PLACEHOLDER");

        Assert.True(vm.Description.IsMixed);
        Assert.Equal("PLACEHOLDER", vm.Description.Value);
    }

    [Fact]
    public void LoadMulti_NullVsEmptyDuration_TreatedAsSame()
    {
        // Both null and "" normalise to "" → unanimous, not mixed.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Duration = null },
            new AlbumTrack { TrackNumber = 2, Duration = ""   },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.False(vm.Duration.IsMixed);
        Assert.Equal("", vm.Duration.Value);
    }

    [Fact]
    public void LoadMulti_SingleTrackInSelection_LoadsAsUnanimous()
    {
        // Degenerate case — multi-edit with a 1-track selection. Every field
        // is trivially unanimous; no Mixed sentinel needed.
        var tracks = new[] { new AlbumTrack { TrackNumber = 9, Description = "Only one" } };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.False(vm.TrackNumber.IsMixed);
        Assert.Equal("9", vm.TrackNumber.Value);
        Assert.False(vm.Description.IsMixed);
        Assert.Equal("Only one", vm.Description.Value);
    }

    [Fact]
    public void LoadMulti_FlacAndMp3Paths_LoadFromFirstTrack()
    {
        // The audio-overrides group is disabled in multi-edit, but the VM
        // still needs initialised values so the TwoWay binding has something
        // to round-trip. LoadMulti uses the first track's values.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, FlacPath = @"C:\First.flac", Mp3Path = @"C:\First.mp3" },
            new AlbumTrack { TrackNumber = 2, FlacPath = @"C:\Second.flac" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.Equal(@"C:\First.flac", vm.FlacPath.Value);
        Assert.Equal(@"C:\First.mp3",  vm.Mp3Path.Value);
        // Whether IsMixed is true here doesn't matter for the disabled-in-UI case;
        // tests below cover the user-relevant fields.
    }

    // ── LoadLoose ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadLoose_PopulatesDisplayedFields_AndForcesTrackNumberSentinel()
    {
        // Loose tracks have TrackNumber=0 sentinel (no disc position). The
        // editor hides the TrackNumber UI, but the VM still holds "0" so the
        // binding has a valid value.
        var track = new AlbumTrack
        {
            TrackNumber = 0,
            Duration    = "8:00",
            Description = "Standalone MP3",
            FlacPath    = @"D:\loose.flac",
            Mp3Path     = @"D:\loose.mp3",
        };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);

        Assert.Equal("0",                  vm.TrackNumber.Value);
        Assert.Equal("8:00",               vm.Duration.Value);
        Assert.Equal("Standalone MP3",     vm.Description.Value);
        Assert.Equal(@"D:\loose.flac",     vm.FlacPath.Value);
        Assert.Equal(@"D:\loose.mp3",      vm.Mp3Path.Value);

        Assert.False(vm.TrackNumber.StartedMixed);
        Assert.False(vm.TrackNumber.WasEdited);
    }

    [Fact]
    public void LoadLoose_NullOptionalFields_NormaliseToEmpty()
    {
        var track = new AlbumTrack { TrackNumber = 0 };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);

        Assert.Equal("0", vm.TrackNumber.Value);
        Assert.Equal("",  vm.Duration.Value);
        Assert.Equal("",  vm.Description.Value);
        Assert.Equal("",  vm.FlacPath.Value);
        Assert.Equal("",  vm.Mp3Path.Value);
    }

    // ── Re-hydration ─────────────────────────────────────────────────────────

    [Fact]
    public void LoadSingle_ReHydrate_ReplacesPreviousValues()
    {
        // Important for the editor's theoretical re-show pattern. Re-loading
        // must replace, not append.
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack { TrackNumber = 1, Description = "First" });
        Assert.Equal("First", vm.Description.Value);

        vm.LoadSingle(new AlbumTrack { TrackNumber = 2, Description = "Second" });

        Assert.Equal("2",       vm.TrackNumber.Value);
        Assert.Equal("Second",  vm.Description.Value);
        Assert.False(vm.Description.WasEdited);   // Init resets WasEdited
    }

    [Fact]
    public void UserEditOnUnanimousField_TripsWasEdited_NotStartedMixed()
    {
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack { TrackNumber = 4, Duration = "3:00" });

        vm.Duration.Value = "3:30";   // simulates the TwoWay binding firing

        Assert.True(vm.Duration.WasEdited);
        Assert.False(vm.Duration.IsMixed);
        Assert.False(vm.Duration.StartedMixed);
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
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack { TrackNumber = 1, SparsCode = input });
        Assert.Equal(expected, vm.SparsCode.Value);
        Assert.False(vm.SparsCode.IsMixed);
    }

    [Theory]
    [InlineData(null,  "Unknown")]
    [InlineData(true,  "Stereo")]
    [InlineData(false, "Mono")]
    public void LoadSingle_IsStereo_ConvertsBoolNullableToStringVocabulary(bool? input, string expected)
    {
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack { TrackNumber = 1, IsStereo = input });
        Assert.Equal(expected, vm.IsStereo.Value);
        Assert.False(vm.IsStereo.IsMixed);
    }

    [Fact]
    public void LoadLoose_SparsCodeAndIsStereo_LoadFromTrack()
    {
        var track = new AlbumTrack
        {
            TrackNumber = 0,
            SparsCode   = "ADD",
            IsStereo    = false,
        };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);

        Assert.Equal("ADD",  vm.SparsCode.Value);
        Assert.Equal("Mono", vm.IsStereo.Value);
    }

    [Fact]
    public void LoadNew_SparsCodeAndIsStereo_DefaultToUnknown()
    {
        var disc = new AlbumDisc { DiscNumber = 1 };
        var vm = new TrackEditorViewModel();
        vm.LoadNew(disc);

        Assert.Equal("Unknown", vm.SparsCode.Value);
        Assert.Equal("Unknown", vm.IsStereo.Value);
        Assert.False(vm.SparsCode.IsMixed);
        Assert.False(vm.IsStereo.IsMixed);
    }

    [Fact]
    public void LoadMulti_DifferingSparsCodes_LoadAsMixedSentinel()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SparsCode = "DDD" },
            new AlbumTrack { TrackNumber = 2, SparsCode = "ADD" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, mixedPlaceholder: "TEXTMIXED");

        // SparsCode uses its OWN sentinel constant, not the text-field one,
        // so the comboboxes can append their distinct ComboBoxItem.
        Assert.True(vm.SparsCode.IsMixed);
        Assert.True(vm.SparsCode.StartedMixed);
        Assert.Equal(TrackEditorViewModel.SparsCodeMixedSentinel, vm.SparsCode.Value);
    }

    [Fact]
    public void LoadMulti_DifferingIsStereo_LoadAsMixedSentinel()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, IsStereo = true },
            new AlbumTrack { TrackNumber = 2, IsStereo = false },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, mixedPlaceholder: "TEXTMIXED");

        Assert.True(vm.IsStereo.IsMixed);
        Assert.True(vm.IsStereo.StartedMixed);
        Assert.Equal(TrackEditorViewModel.IsStereoMixedSentinel, vm.IsStereo.Value);
    }

    [Fact]
    public void LoadMulti_NullSparsCodeUnanimousWithEmpty_TreatedAsSame()
    {
        // Both null and "" map to "Unknown" under SparsCodeToString — so a
        // multi-edit where one track has null and another has "" SparsCode
        // shouldn't show as Mixed.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SparsCode = null },
            new AlbumTrack { TrackNumber = 2, SparsCode = ""   },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.False(vm.SparsCode.IsMixed);
        Assert.Equal("Unknown", vm.SparsCode.Value);
    }

    [Theory]
    [InlineData("DDD",      "DDD")]
    [InlineData("Unknown",  "Unknown")]   // "Unknown" stores as the literal string
    [InlineData("",         null)]
    [InlineData(null,       null)]
    public void SparsCodeFromString_RoundTripBehaviour(string? input, string? expected)
    {
        Assert.Equal(expected, TrackEditorViewModel.SparsCodeFromString(input));
    }

    [Theory]
    [InlineData("Stereo",     true)]
    [InlineData("Mono",       false)]
    [InlineData("Unknown",    null)]
    [InlineData("Mixed",      null)]   // sentinel maps to null too (defensive)
    [InlineData("",           null)]
    [InlineData(null,         null)]
    public void IsStereoFromString_RoundTripBehaviour(string? input, bool? expected)
    {
        Assert.Equal(expected, TrackEditorViewModel.IsStereoFromString(input));
    }

    // ── Session combo retired ────────────────────────────────────────────────
    // The Session combo and its SessionId/SessionIndex plumbing were removed
    // in the sessions-as-fields refactor; per-track session fields live as
    // MixedField<string>s now. Tests for the new flat fields would belong
    // in TrackEditorViewModelSaveTests / TrackEditorViewModelTests once
    // explicit album-default coverage is added.

    // ── Slice 4: PieceRefs + Performers ──────────────────────────────────────

    [Fact]
    public void LoadSingle_PopulatesPieceRefsAndPerformers_FromTrack()
    {
        var track = new AlbumTrack
        {
            TrackNumber = 1,
            PieceRefs   = new List<TrackPieceRef>
            {
                new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Symphony 9" },
            },
            Performers  = new List<AlbumPerformer>
            {
                new() { Name = "Karajan, Herbert von", Role = "Conductor" },
            },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(track);

        Assert.Single(vm.PieceRefs.Items);
        Assert.Equal("Symphony 9", vm.PieceRefs.Items[0].PieceTitle);
        Assert.Single(vm.Performers.Items);
        Assert.Equal("Karajan, Herbert von", vm.Performers.Items[0].Name);
        Assert.False(vm.PieceRefs.StartedMixed);
        Assert.False(vm.Performers.StartedMixed);
    }

    [Fact]
    public void LoadSingle_NullCollections_LeaveEmpty()
    {
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack { TrackNumber = 1 });

        Assert.Empty(vm.PieceRefs.Items);
        Assert.Empty(vm.Performers.Items);
    }

    [Fact]
    public void LoadLoose_PopulatesPieceRefsAndPerformers_FromTrack()
    {
        var track = new AlbumTrack
        {
            TrackNumber = 0,
            PieceRefs   = new List<TrackPieceRef>
            {
                new() { Composer = "Brendel", PieceTitle = "Interview" },
            },
            Performers  = new List<AlbumPerformer> { new() { Name = "Brendel, Alfred" } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);

        Assert.Single(vm.PieceRefs.Items);
        Assert.Single(vm.Performers.Items);
    }

    [Fact]
    public void LoadMulti_UnanimousLists_LoadAsUnanimous()
    {
        var sharedRefs = new List<TrackPieceRef>
        {
            new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Sonata 14" },
        };
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, PieceRefs = JsonSerializer.Deserialize<List<TrackPieceRef>>(JsonSerializer.Serialize(sharedRefs)) },
            new AlbumTrack { TrackNumber = 2, PieceRefs = JsonSerializer.Deserialize<List<TrackPieceRef>>(JsonSerializer.Serialize(sharedRefs)) },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.False(vm.PieceRefs.StartedMixed);
        Assert.Single(vm.PieceRefs.Items);
        Assert.Equal("Sonata 14", vm.PieceRefs.Items[0].PieceTitle);
    }

    [Fact]
    public void LoadMulti_DifferingLists_LoadAsMixed()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, PieceRefs = new List<TrackPieceRef> { new() { PieceTitle = "A" } } },
            new AlbumTrack { TrackNumber = 2, PieceRefs = new List<TrackPieceRef> { new() { PieceTitle = "B" } } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.True(vm.PieceRefs.StartedMixed);
        Assert.False(vm.PieceRefs.WasEdited);
        Assert.Empty(vm.PieceRefs.Items);
        Assert.False(vm.PieceRefs.ShouldWriteOnSave);   // skip save
    }

    [Fact]
    public void LoadMulti_NullVsEmptyLists_TreatedAsSame()
    {
        // Both null and an empty list normalise to "[]" — unanimous.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, PieceRefs = null },
            new AlbumTrack { TrackNumber = 2, PieceRefs = new List<TrackPieceRef>() },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        Assert.False(vm.PieceRefs.StartedMixed);
        Assert.Empty(vm.PieceRefs.Items);
    }

    [Fact]
    public void LoadMulti_UserAddsToMixedList_FlipsWasEdited_AndAllowsSave()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Performers = new List<AlbumPerformer> { new() { Name = "A" } } },
            new AlbumTrack { TrackNumber = 2, Performers = new List<AlbumPerformer> { new() { Name = "B" } } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        Assert.False(vm.Performers.ShouldWriteOnSave);   // started Mixed, untouched → skip

        vm.Performers.Items.Add(new AlbumPerformer { Name = "New shared performer" });

        Assert.True(vm.Performers.WasEdited);
        Assert.True(vm.Performers.ShouldWriteOnSave);   // touched → write
        Assert.True(vm.Performers.StartedMixed);        // history bit stays
    }

    [Fact]
    public void LoadSingle_ReHydratesAlreadyPopulatedCollections()
    {
        // Re-loading the editor with a different track must clear the
        // previous lists, not append to them.
        var vm = new TrackEditorViewModel();
        vm.LoadSingle(new AlbumTrack
        {
            TrackNumber = 1,
            PieceRefs   = new List<TrackPieceRef> { new() { PieceTitle = "First" } },
        });
        Assert.Single(vm.PieceRefs.Items);

        vm.LoadSingle(new AlbumTrack
        {
            TrackNumber = 2,
            PieceRefs   = new List<TrackPieceRef> { new() { PieceTitle = "Second" } },
        });

        Assert.Single(vm.PieceRefs.Items);
        Assert.Equal("Second", vm.PieceRefs.Items[0].PieceTitle);
        Assert.False(vm.PieceRefs.WasEdited);   // Init resets
    }

    [Fact]
    public void LoadMulti_UnanimousLists_VmOwnsIndependentCopy()
    {
        // When all tracks share a list, LoadMulti must NOT bind the VM
        // collection to the source list — user Add/Remove should not mutate
        // any track's list directly.
        var sharedList = new List<AlbumPerformer> { new() { Name = "Karajan" } };
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Performers = sharedList },
            new AlbumTrack { TrackNumber = 2, Performers = sharedList },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        vm.Performers.Items.Add(new AlbumPerformer { Name = "Late add" });

        // Source list unchanged — VM owns its own copy.
        Assert.Single(sharedList);
        Assert.Equal(2, vm.Performers.Items.Count);
    }
}
