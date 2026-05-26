using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 TrackEditor (slice 5): <see cref="TrackEditorViewModel.SaveSingle"/>,
/// <see cref="TrackEditorViewModel.SaveLoose"/> and
/// <see cref="TrackEditorViewModel.SaveMulti"/> move the data-mutation pass
/// off the editor's code-behind. These tests lock the contract for each.
/// </summary>
public class TrackEditorViewModelSaveTests
{
    // ── SaveSingle ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveSingle_ValidTrackNumber_WritesAllFieldsToExistingTrack()
    {
        var existing = new AlbumTrack { TrackNumber = 7 };
        var disc = new AlbumDisc { DiscNumber = 1, Tracks = { existing } };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(existing);
        vm.TrackNumber.Value = "9";
        vm.Duration.Value    = "4:20";
        vm.Description.Value = "Allegro";
        vm.SparsCode.Value   = "DDD";
        vm.IsStereo.Value    = "Stereo";

        var error = vm.SaveSingle(disc, 0);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, error);
        Assert.Equal(9,       existing.TrackNumber);
        Assert.Equal("4:20",  existing.Duration);
        Assert.Equal("Allegro", existing.Description);
        Assert.Equal("DDD",   existing.SparsCode);
        Assert.True(existing.IsStereo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("0")]      // not positive
    [InlineData("-3")]     // not positive
    [InlineData("abc")]    // not parseable
    public void SaveSingle_InvalidTrackNumber_ReturnsValidationError_AndLeavesTrackUnmutated(string input)
    {
        var existing = new AlbumTrack { TrackNumber = 5, Description = "original" };
        var disc = new AlbumDisc { DiscNumber = 1, Tracks = { existing } };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(existing);
        vm.TrackNumber.Value = input;
        vm.Description.Value = "would-be-new";

        var error = vm.SaveSingle(disc, 0);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.InvalidTrackNumber, error);
        Assert.Equal(5, existing.TrackNumber);                // unchanged
        Assert.Equal("original", existing.Description);       // unchanged
    }

    [Fact]
    public void SaveSingle_AddNewMode_AppendsTrack()
    {
        // trackIndex >= Tracks.Count means "add new". A fresh AlbumTrack is
        // created, populated from VM, and appended.
        var disc = new AlbumDisc { DiscNumber = 1, Tracks = { new() { TrackNumber = 1 } } };

        var vm = new TrackEditorViewModel();
        vm.LoadNew(disc);                         // TrackNumber defaults to 2
        vm.Description.Value = "New track";

        var error = vm.SaveSingle(disc, disc.Tracks.Count);   // index == count = add-new

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, error);
        Assert.Equal(2, disc.Tracks.Count);
        Assert.Equal(2, disc.Tracks[1].TrackNumber);
        Assert.Equal("New track", disc.Tracks[1].Description);
    }

    [Fact]
    public void SaveSingle_SnapshotsLists_IntoFreshListInstances()
    {
        // The Items collection is the VM's own ObservableCollection — saving
        // must not bind the model field to it. Subsequent VM mutations should
        // not affect the saved track.
        var existing = new AlbumTrack { TrackNumber = 1 };
        var disc = new AlbumDisc { DiscNumber = 1, Tracks = { existing } };

        var vm = new TrackEditorViewModel();
        vm.LoadSingle(existing);
        vm.Performers.Items.Add(new AlbumPerformer { Name = "X" });

        vm.SaveSingle(disc, 0);
        Assert.Single(existing.Performers!);

        vm.Performers.Items.Add(new AlbumPerformer { Name = "Y" });
        Assert.Single(existing.Performers!);   // saved list untouched
    }

    // ── SaveLoose ────────────────────────────────────────────────────────────

    [Fact]
    public void SaveLoose_ForcesTrackNumberZeroAndSessionIndexNull()
    {
        var track = new AlbumTrack { TrackNumber = 99, SessionIndex = 7 };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);
        vm.Description.Value = "Loose";

        vm.SaveLoose(track);

        Assert.Equal(0, track.TrackNumber);
        Assert.Null(track.SessionIndex);
        Assert.Equal("Loose", track.Description);
    }

    [Fact]
    public void SaveLoose_WritesScalarsAndLists()
    {
        var track = new AlbumTrack { TrackNumber = 0 };

        var vm = new TrackEditorViewModel();
        vm.LoadLoose(track);
        vm.Duration.Value    = "10:00";
        vm.SparsCode.Value   = "AAD";
        vm.IsStereo.Value    = "Mono";
        vm.FlacPath.Value    = @"D:\loose.flac";
        vm.Mp3Path.Value     = @"D:\loose.mp3";
        vm.Performers.Items.Add(new AlbumPerformer { Name = "Brendel, Alfred" });

        vm.SaveLoose(track);

        Assert.Equal("10:00",         track.Duration);
        Assert.Equal("AAD",           track.SparsCode);
        Assert.False(track.IsStereo);
        Assert.Equal(@"D:\loose.flac", track.FlacPath);
        Assert.Equal(@"D:\loose.mp3",  track.Mp3Path);
        Assert.Single(track.Performers!);
        Assert.Equal("Brendel, Alfred", track.Performers![0].Name);
    }

    // ── SaveMulti ────────────────────────────────────────────────────────────

    [Fact]
    public void SaveMulti_AllLoose_SkipsTrackNumberValidation()
    {
        // Loose tracks have TrackNumber=0 — pre-fix bug rejected them as
        // not-positive. With allLoose=true we don't validate or write
        // TrackNumber at all.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 0, Description = "A" },
            new AlbumTrack { TrackNumber = 0, Description = "B" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        // Description happens to be Mixed; user types "Shared".
        vm.Description.Value = "Shared";

        var error = vm.SaveMulti(tracks, allLoose: true);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, error);
        Assert.Equal(0, tracks[0].TrackNumber);   // unchanged loose sentinel
        Assert.Equal(0, tracks[1].TrackNumber);
        Assert.Equal("Shared", tracks[0].Description);
        Assert.Equal("Shared", tracks[1].Description);
    }

    [Fact]
    public void SaveMulti_UnanimousTrackNumber_ButStillInvalid_ReturnsValidationError()
    {
        // Two tracks both with TrackNumber=0 (impossible for album-bound but
        // models the "allLoose=false yet TrackNumber is 0" case). The
        // !SkipMixedTextWrite branch fires (StartedMixed=false), validation
        // rejects 0.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 0 },
            new AlbumTrack { TrackNumber = 0 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        var error = vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.InvalidTrackNumber, error);
    }

    [Fact]
    public void SaveMulti_MixedTrackNumber_UserDidntTouch_SkipsWrite()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1 },
            new AlbumTrack { TrackNumber = 2 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        // User doesn't touch TrackNumber.

        var error = vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, error);
        Assert.Equal(1, tracks[0].TrackNumber);    // unchanged
        Assert.Equal(2, tracks[1].TrackNumber);
    }

    [Fact]
    public void SaveMulti_MixedTrackNumber_UserTypedPositive_WritesToAll()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1 },
            new AlbumTrack { TrackNumber = 2 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        vm.TrackNumber.Value = "5";

        var error = vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal(TrackEditorViewModel.SaveValidationError.None, error);
        Assert.Equal(5, tracks[0].TrackNumber);
        Assert.Equal(5, tracks[1].TrackNumber);
    }

    [Fact]
    public void SaveMulti_MixedPerformers_UserAdds_AppendsToEachTracksExistingList()
    {
        // The H13 slice 4 additive contract: Mixed list + user Add → APPEND
        // to each track's existing list, preserving prior entries.
        var t1Original = new AlbumPerformer { Name = "Karajan" };
        var t2Original = new AlbumPerformer { Name = "Bernstein" };
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Performers = new List<AlbumPerformer> { t1Original } },
            new AlbumTrack { TrackNumber = 2, Performers = new List<AlbumPerformer> { t2Original } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        Assert.True(vm.Performers.StartedMixed);
        vm.Performers.Items.Add(new AlbumPerformer { Name = "Bohm" });

        vm.SaveMulti(tracks, allLoose: false);

        // Each track preserves its original AND gets the appended Bohm.
        Assert.Equal(2, tracks[0].Performers!.Count);
        Assert.Equal("Karajan", tracks[0].Performers![0].Name);
        Assert.Equal("Bohm",    tracks[0].Performers![1].Name);
        Assert.Equal(2, tracks[1].Performers!.Count);
        Assert.Equal("Bernstein", tracks[1].Performers![0].Name);
        Assert.Equal("Bohm",      tracks[1].Performers![1].Name);
    }

    [Fact]
    public void SaveMulti_MixedPerformers_UserDidNothing_SkipsWrite()
    {
        var t1Original = new AlbumPerformer { Name = "Karajan" };
        var t2Original = new AlbumPerformer { Name = "Bernstein" };
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Performers = new List<AlbumPerformer> { t1Original } },
            new AlbumTrack { TrackNumber = 2, Performers = new List<AlbumPerformer> { t2Original } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        // No user touch.

        vm.SaveMulti(tracks, allLoose: false);

        // Each track unchanged.
        Assert.Single(tracks[0].Performers!);
        Assert.Equal("Karajan", tracks[0].Performers![0].Name);
        Assert.Single(tracks[1].Performers!);
        Assert.Equal("Bernstein", tracks[1].Performers![0].Name);
    }

    [Fact]
    public void SaveMulti_UnanimousPerformers_ReplacesEachTracksList_WithFreshCopy()
    {
        // Two tracks have the same performers (the JSON fingerprints match)
        // — loaded as Unanimous. Save replaces each track's list with a
        // fresh copy.
        var p1 = new AlbumPerformer { Name = "Solti" };
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Performers = new List<AlbumPerformer> { p1 } },
            new AlbumTrack { TrackNumber = 2, Performers = new List<AlbumPerformer> { new() { Name = "Solti" } } },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        Assert.False(vm.Performers.StartedMixed);

        vm.SaveMulti(tracks, allLoose: false);

        // Each track has its own list instance (not the same reference).
        Assert.NotSame(tracks[0].Performers, tracks[1].Performers);
        Assert.Equal("Solti", tracks[0].Performers![0].Name);
        Assert.Equal("Solti", tracks[1].Performers![0].Name);
    }

    [Fact]
    public void SaveMulti_TextFieldMixedClearedWithoutTyping_DoesNotWipe()
    {
        // The "don't wipe on backspace" safety: user backspaces through the
        // "Mixed" placeholder but doesn't type a replacement. Value goes to
        // "" but StartedMixed stays true → skip the write.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, Duration = "3:00" },
            new AlbumTrack { TrackNumber = 2, Duration = "4:00" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        vm.Duration.Value = "";   // backspace-through

        vm.SaveMulti(tracks, allLoose: false);

        // Neither track wiped.
        Assert.Equal("3:00", tracks[0].Duration);
        Assert.Equal("4:00", tracks[1].Duration);
    }

    [Fact]
    public void SaveMulti_MixedSparsCode_UserDidNotTouch_SkipsWrite()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SparsCode = "DDD" },
            new AlbumTrack { TrackNumber = 2, SparsCode = "ADD" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal("DDD", tracks[0].SparsCode);
        Assert.Equal("ADD", tracks[1].SparsCode);
    }

    [Fact]
    public void SaveMulti_MixedSparsCode_UserPicked_WritesToAll()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SparsCode = "DDD" },
            new AlbumTrack { TrackNumber = 2, SparsCode = "ADD" },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");
        vm.SparsCode.Value = "AAD";   // user picks

        vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal("AAD", tracks[0].SparsCode);
        Assert.Equal("AAD", tracks[1].SparsCode);
    }

    [Fact]
    public void SaveMulti_MixedSession_UserDidNotTouch_SkipsWrite()
    {
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SessionIndex = 0 },
            new AlbumTrack { TrackNumber = 2, SessionIndex = 1 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)");

        vm.SaveMulti(tracks, allLoose: false);

        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Equal(1, tracks[1].SessionIndex);
    }

    [Fact]
    public void SaveMulti_UnsharedSessions_SkipsSessionWrite()
    {
        // hasSharedSessions=false → Session loaded as Mixed → save skips.
        var tracks = new[]
        {
            new AlbumTrack { TrackNumber = 1, SessionIndex = 0 },
            new AlbumTrack { TrackNumber = 2, SessionIndex = 0 },
        };

        var vm = new TrackEditorViewModel();
        vm.LoadMulti(tracks, "(Mixed)", hasSharedSessions: false);

        vm.SaveMulti(tracks, allLoose: false);

        // SessionIndex untouched even though both tracks happen to share the value.
        Assert.Equal(0, tracks[0].SessionIndex);
        Assert.Equal(0, tracks[1].SessionIndex);
    }
}
