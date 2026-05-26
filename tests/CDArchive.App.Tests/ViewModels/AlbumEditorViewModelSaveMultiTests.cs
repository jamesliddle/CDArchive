using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 (slice 4): <see cref="AlbumEditorViewModel.SaveMulti"/> moves the
/// multi-edit album-write pass off the editor's code-behind. The skip-when-
/// still-mixed contract and the cleared-without-typing safe-guard both live
/// in the VM now and are unit-testable without WPF.
/// </summary>
public class AlbumEditorViewModelSaveMultiTests
{
    // ── Text-field contract ──────────────────────────────────────────────────

    [Fact]
    public void SaveMulti_StartedMixedField_UserDidNotTouch_SkipsWrite()
    {
        // Two albums with differing Titles → VM loads Title Mixed. User never
        // touches the Title field. Save must NOT propagate the placeholder
        // string to either album's Title.
        var a1 = new CanonAlbum { Title = "A" };
        var a2 = new CanonAlbum { Title = "B" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        Assert.True(vm.Title.IsMixed);   // sanity

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("A", a1.Title);     // unchanged
        Assert.Equal("B", a2.Title);
    }

    [Fact]
    public void SaveMulti_StartedMixedField_UserClearedWithoutTyping_SkipsWrite()
    {
        // This is the slice-1 safety contract: clearing the "Mixed" placeholder
        // without typing a replacement must not wipe every album to empty.
        var a1 = new CanonAlbum { Title = "A" };
        var a2 = new CanonAlbum { Title = "B" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        // Simulate the MixedPlaceholder clear: Value goes to "" but the user
        // doesn't type anything new.
        vm.Title.Value = "";

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("A", a1.Title);   // unchanged
        Assert.Equal("B", a2.Title);
    }

    [Fact]
    public void SaveMulti_StartedMixedField_UserTypedSomething_WritesToAllAlbums()
    {
        var a1 = new CanonAlbum { Title = "A" };
        var a2 = new CanonAlbum { Title = "B" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        vm.Title.Value = "Unified";

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("Unified", a1.Title);
        Assert.Equal("Unified", a2.Title);
    }

    [Fact]
    public void SaveMulti_UnanimousField_LeftEmpty_WritesNullToAllAlbums()
    {
        // For a field that started Unanimous (both albums have "X"), the user
        // is allowed to deliberately wipe by editing to empty — that intent
        // must propagate. NullIfEmpty maps "" → null.
        var a1 = new CanonAlbum { Title = "T", Notes = "shared note" };
        var a2 = new CanonAlbum { Title = "T", Notes = "shared note" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        Assert.False(vm.Notes.IsMixed);   // sanity: started unanimous
        vm.Notes.Value = "";

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Null(a1.Notes);
        Assert.Null(a2.Notes);
    }

    [Fact]
    public void SaveMulti_UnanimousField_Unchanged_WritesUnchangedValue()
    {
        // Unanimous + user didn't touch → still writes the (unchanged) value.
        // Idempotent — round-tripping through SaveMulti must not mutate.
        var a1 = new CanonAlbum { Title = "T", Label = "DG" };
        var a2 = new CanonAlbum { Title = "T", Label = "DG" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("DG", a1.Label);
        Assert.Equal("DG", a2.Label);
    }

    // ── ComboBox-field contract (SparsCode, IsStereo) ────────────────────────

    [Fact]
    public void SaveMulti_StartedMixedSparsCode_UserDidNotTouch_SkipsWrite()
    {
        var a1 = new CanonAlbum { Title = "T", SparsCode = "DDD" };
        var a2 = new CanonAlbum { Title = "T", SparsCode = "ADD" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        Assert.True(vm.SparsCode.IsMixed);   // sanity

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("DDD", a1.SparsCode);
        Assert.Equal("ADD", a2.SparsCode);
    }

    [Fact]
    public void SaveMulti_StartedMixedSparsCode_UserPicked_WritesToAllAlbums()
    {
        var a1 = new CanonAlbum { Title = "T", SparsCode = "DDD" };
        var a2 = new CanonAlbum { Title = "T", SparsCode = "ADD" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        // The view's SelectionChanged handler sets VM.SparsCode.Value, which
        // trips IsMixed=false via the partial-method hook on Value.
        vm.SparsCode.Value = "AAD";

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("AAD", a1.SparsCode);
        Assert.Equal("AAD", a2.SparsCode);
    }

    [Fact]
    public void SaveMulti_StartedMixedIsStereo_UserPicked_WritesToAllAlbums()
    {
        var a1 = new CanonAlbum { Title = "T", IsStereo = true };
        var a2 = new CanonAlbum { Title = "T", IsStereo = false };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        vm.IsStereo.Value = "Mono";

        vm.SaveMulti(new[] { a1, a2 });

        Assert.False(a1.IsStereo);
        Assert.False(a2.IsStereo);
    }

    [Fact]
    public void SaveMulti_UnanimousSparsCode_UserUntouched_WritesUnchangedValue()
    {
        var a1 = new CanonAlbum { Title = "T", SparsCode = "DDD" };
        var a2 = new CanonAlbum { Title = "T", SparsCode = "DDD" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        Assert.False(vm.SparsCode.IsMixed);   // sanity

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("DDD", a1.SparsCode);
        Assert.Equal("DDD", a2.SparsCode);
    }

    // ── Track-backfill contract ──────────────────────────────────────────────

    [Fact]
    public void SaveMulti_TouchedSparsCode_PushesToEveryTrack_OverwritingOverrides()
    {
        var t1a = new AlbumTrack { TrackNumber = 1, SparsCode = "ADD" };   // override
        var t1b = new AlbumTrack { TrackNumber = 2 };                       // null
        var t2a = new AlbumTrack { TrackNumber = 1 };                       // null
        var a1 = new CanonAlbum
        {
            Title = "T1",
            SparsCode = "DDD",
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t1a, t1b } } },
        };
        var a2 = new CanonAlbum
        {
            Title = "T2",
            SparsCode = "ADD",
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t2a } } },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        vm.SparsCode.Value = "AAD";   // user picks something — pushes to every track

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("AAD", t1a.SparsCode);   // override overwritten
        Assert.Equal("AAD", t1b.SparsCode);   // null filled
        Assert.Equal("AAD", t2a.SparsCode);   // null filled in second album
    }

    [Fact]
    public void SaveMulti_UntouchedSparsCode_BackfillsNullTracks_PreservesOverrides()
    {
        // SparsCode was Mixed; user didn't touch it. Per-album backfill: null
        // tracks get their own album's SparsCode; non-null tracks unchanged.
        var t1Override = new AlbumTrack { TrackNumber = 1, SparsCode = "ADD" };   // keep
        var t1Null     = new AlbumTrack { TrackNumber = 2 };                       // backfill from a1
        var t2Null     = new AlbumTrack { TrackNumber = 1 };                       // backfill from a2
        var a1 = new CanonAlbum
        {
            Title = "T1",
            SparsCode = "DDD",
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t1Override, t1Null } } },
        };
        var a2 = new CanonAlbum
        {
            Title = "T2",
            SparsCode = "AAD",
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t2Null } } },
        };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");
        // No SparsCode edit.

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("ADD", t1Override.SparsCode);   // unchanged
        Assert.Equal("DDD", t1Null.SparsCode);       // backfilled from a1
        Assert.Equal("AAD", t2Null.SparsCode);       // backfilled from a2
    }

    [Fact]
    public void SaveMulti_NullTrackPerformers_BackfilledFromAlbumPerformers()
    {
        // Performers tab is hidden in multi-edit. The per-track null backfill
        // still runs: a track with null Performers receives a deep-cloned copy
        // of its album's performer list.
        var a1Perf = new AlbumPerformer { Name = "Karajan" };
        var t1 = new AlbumTrack { TrackNumber = 1 };
        var a1 = new CanonAlbum
        {
            Title = "T1",
            Performers = new List<AlbumPerformer> { a1Perf },
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t1 } } },
        };
        var a2 = new CanonAlbum { Title = "T2" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");

        vm.SaveMulti(new[] { a1, a2 });

        Assert.NotNull(t1.Performers);
        Assert.Single(t1.Performers!);
        Assert.Equal("Karajan", t1.Performers[0].Name);
        // Independent instance — mutating the album's performer must not
        // affect the track's clone (the propagator deep-clones).
        Assert.NotSame(a1Perf, t1.Performers[0]);
    }

    [Fact]
    public void SaveMulti_TrackWithOwnPerformers_NotOverwritten()
    {
        // Multi-edit only backfills nulls; an existing track-level override is
        // preserved (no UI to edit it in bulk).
        var t1 = new AlbumTrack
        {
            TrackNumber = 1,
            Performers  = new List<AlbumPerformer> { new() { Name = "Track-level override" } },
        };
        var a1 = new CanonAlbum
        {
            Title = "T1",
            Performers = new List<AlbumPerformer> { new() { Name = "Album perf" } },
            Discs = { new AlbumDisc { DiscNumber = 1, Tracks = { t1 } } },
        };
        var a2 = new CanonAlbum { Title = "T2" };

        var vm = new AlbumEditorViewModel();
        vm.LoadMulti(new[] { a1, a2 }, "(Mixed)");

        vm.SaveMulti(new[] { a1, a2 });

        Assert.Equal("Track-level override", t1.Performers![0].Name);
    }
}
