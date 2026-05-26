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
}
