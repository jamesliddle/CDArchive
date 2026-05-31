using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// H13 (slice 4): <see cref="AlbumEditorViewModel.SaveSingle"/> moves the
/// single-edit album-write pass off the editor's code-behind. The VM
/// validates Title, writes every scalar field into the album, snapshots
/// Performers + Sessions into the album's List fields, removes empty discs,
/// and runs the inheritable-field propagator.
/// </summary>
public class AlbumEditorViewModelSaveSingleTests
{
    private static AlbumFieldPropagator.InheritableSnapshot SnapshotOf(CanonAlbum a) =>
        AlbumFieldPropagator.Snapshot(a);

    [Fact]
    public void SaveSingle_ValidTitle_WritesAllScalarFieldsToAlbum()
    {
        var album = new CanonAlbum();
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);

        // Simulate the user filling in every field.
        vm.Title.Value           = "Symphony 9";
        vm.Subtitle.Value        = "Choral";
        vm.Label.Value           = "DG";
        vm.CatalogueNumber.Value = "447-401";
        vm.Barcode.Value         = "028944740127";
        vm.ArchiveFolder.Value   = "Beethoven Sym 9 Karajan";
        vm.Notes.Value           = "1962 cycle";
        vm.SparsCode.Value       = "ADD";
        vm.IsStereo.Value        = "Stereo";

        var error = vm.SaveSingle(album, SnapshotOf(album));

        Assert.Equal(AlbumEditorViewModel.SaveValidationError.None, error);
        Assert.Equal("Symphony 9",               album.Title);
        Assert.Equal("Choral",                   album.Subtitle);
        Assert.Equal("DG",                       album.Label);
        Assert.Equal("447-401",                  album.CatalogueNumber);
        Assert.Equal("028944740127",             album.Barcode);
        Assert.Equal("Beethoven Sym 9 Karajan",  album.ArchiveFolder);
        Assert.Equal("1962 cycle",               album.Notes);
        Assert.Equal("ADD",                      album.SparsCode);
        Assert.True(album.IsStereo);
    }

    [Fact]
    public void SaveSingle_EmptyTitle_ReturnsMissingTitle_AndLeavesAlbumUnmutated()
    {
        var album = new CanonAlbum { Title = "original", Label = "OriginalLabel" };
        var snap  = SnapshotOf(album);
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "   ";   // whitespace counts as empty
        vm.Label.Value = "ChangedLabel";

        var error = vm.SaveSingle(album, snap);

        Assert.Equal(AlbumEditorViewModel.SaveValidationError.MissingTitle, error);
        Assert.Equal("original",      album.Title);   // unmutated
        Assert.Equal("OriginalLabel", album.Label);   // unmutated
    }

    [Fact]
    public void SaveSingle_TrimsTitle()
    {
        var album = new CanonAlbum();
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "  Padded Title  ";

        var error = vm.SaveSingle(album, SnapshotOf(album));

        Assert.Equal(AlbumEditorViewModel.SaveValidationError.None, error);
        Assert.Equal("Padded Title", album.Title);
    }

    [Fact]
    public void SaveSingle_EmptyOptionalStringFields_MapToNull()
    {
        // The album model uses nullable string for optional fields; saving an
        // empty TextBox should store null (not ""), so JSON snapshots stay
        // compact and round-tripping doesn't churn "" ↔ null.
        var album = new CanonAlbum();
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "T";
        // Leave the rest empty.

        var error = vm.SaveSingle(album, SnapshotOf(album));

        Assert.Equal(AlbumEditorViewModel.SaveValidationError.None, error);
        Assert.Null(album.Subtitle);
        Assert.Null(album.Label);
        Assert.Null(album.CatalogueNumber);
        Assert.Null(album.Barcode);
        Assert.Null(album.ArchiveFolder);
        Assert.Null(album.Notes);
    }

    [Fact]
    public void SaveSingle_SnapshotsPerformersFromVmToAlbumList()
    {
        var album = new CanonAlbum();
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "T";
        vm.Performers.Add(new AlbumPerformer { Name = "Karajan", Role = "Conductor" });
        vm.Performers.Add(new AlbumPerformer { Name = "BPO" });

        vm.SaveSingle(album, SnapshotOf(album));

        Assert.NotNull(album.Performers);
        Assert.Equal(2, album.Performers!.Count);
        Assert.Equal("Karajan", album.Performers[0].Name);
        // Independent List instance — adding to the VM after save must not
        // mutate the album's stored list.
        vm.Performers.Add(new AlbumPerformer { Name = "Late add" });
        Assert.Equal(2, album.Performers.Count);
    }

    [Fact]
    public void SaveSingle_EmptyPerformersCollection_WritesNullToAlbum()
    {
        // Keeps the JSON shape compact — an empty list and null are
        // semantically equivalent for the model but null serialises absent.
        var album = new CanonAlbum { Performers = new List<AlbumPerformer> { new() { Name = "old" } } };
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "T";
        vm.Performers.Clear();

        vm.SaveSingle(album, SnapshotOf(album));

        Assert.Null(album.Performers);
    }

    [Fact]
    public void SaveSingle_WritesSessionFlatFieldsFromVmToAlbum()
    {
        // Post-refactor: session data is direct fields on the album, not a
        // list. Engineers and Producers are ObservableCollections that
        // snapshot to List<string>? on save.
        var album = new CanonAlbum();
        var vm    = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value         = "T";
        vm.SessionDates.Value  = "1962";
        vm.SessionVenue.Value  = "JC Kirche";
        vm.SessionEngineers.Add("Karl-Heinz Schneider");
        vm.SessionProducers.Add("John Culshaw");

        vm.SaveSingle(album, SnapshotOf(album));

        Assert.Equal("1962", album.SessionDates);
        Assert.Equal("JC Kirche", album.SessionVenue);
        Assert.Equal(new[] { "Karl-Heinz Schneider" }, album.SessionEngineers);
        Assert.Equal(new[] { "John Culshaw" },          album.SessionProducers);
    }

    [Fact]
    public void SaveSingle_RemovesEmptyDiscs()
    {
        var album = new CanonAlbum();
        album.Discs.Add(new AlbumDisc { DiscNumber = 1, Tracks = { new() { TrackNumber = 1 } } });
        album.Discs.Add(new AlbumDisc { DiscNumber = 2 });   // empty
        album.Discs.Add(new AlbumDisc { DiscNumber = 3, Tracks = { new() { TrackNumber = 1 } } });

        var vm = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "T";

        vm.SaveSingle(album, SnapshotOf(album));

        Assert.Equal(2, album.Discs.Count);
        Assert.Equal(new[] { 1, 3 }, album.Discs.Select(d => d.DiscNumber));
    }

    [Fact]
    public void SaveSingle_PropagatesChangedSparsCodeDownToTracks()
    {
        // Snapshot at load shows SparsCode=null; user changes album to "DDD";
        // propagator should push to every track.
        var album = new CanonAlbum();
        var t1 = new AlbumTrack { TrackNumber = 1, SparsCode = "ADD" };   // prior override
        var t2 = new AlbumTrack { TrackNumber = 2 };                       // null
        album.Discs.Add(new AlbumDisc { DiscNumber = 1, Tracks = { t1, t2 } });

        var snap = SnapshotOf(album);
        var vm   = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value     = "T";
        vm.SparsCode.Value = "DDD";

        vm.SaveSingle(album, snap);

        Assert.Equal("DDD", t1.SparsCode);   // pushed, prior override overwritten
        Assert.Equal("DDD", t2.SparsCode);   // pushed
    }

    [Fact]
    public void SaveSingle_BackfillsNullTrackFields_FromUnchangedAlbum()
    {
        // Album-level SparsCode unchanged ("DDD" → "DDD"), but t2 has null
        // → backfill with the album's current value.
        var album = new CanonAlbum { SparsCode = "DDD" };
        var t1 = new AlbumTrack { TrackNumber = 1, SparsCode = "ADD" };
        var t2 = new AlbumTrack { TrackNumber = 2 };
        album.Discs.Add(new AlbumDisc { DiscNumber = 1, Tracks = { t1, t2 } });

        var snap = SnapshotOf(album);
        var vm   = new AlbumEditorViewModel();
        vm.LoadSingle(album);
        vm.Title.Value = "T";

        vm.SaveSingle(album, snap);

        Assert.Equal("ADD", t1.SparsCode);   // override preserved (unchanged album)
        Assert.Equal("DDD", t2.SparsCode);   // null backfilled with album value
    }
}
