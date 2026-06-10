using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level tests for the four <c>Complete*EditAsync</c> methods H2 slice 5
/// extracted off <c>CanonView</c>'s code-behind. Pre-slice the View's
/// <c>EditXxxAsync</c> methods owned the entire post-dialog orchestration
/// (rename propagation + catalog reorder + save + album-ref rename + status
/// message); the four <c>Complete*EditAsync</c> methods now carry that work
/// and the View shrinks to dialog construction + a one-line forward.
///
/// <para>The dialog-construction half of each EditXxxAsync stays in the View
/// — <see cref="Window.GetWindow"/> for Owner-getting, composerNames/
/// composerCatalogs/ancestorRoles lookups — and is smoke-tested.</para>
/// </summary>
public class CanonViewModelEditFlowTests
{
    private static CanonViewModel Build(out ICanonDataService svc, out AlbumsViewModel albumsVm)
    {
        svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());
        var dialogs  = new RecordingDialogService();
        albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    // ── CaptureComposerSnapshot ──────────────────────────────────────────────

    [Fact]
    public void CaptureComposerSnapshot_CapturesNameAndPrefixesByValue_NotByReference()
    {
        // Snapshot must outlive subsequent in-place mutations to the composer
        // (the editor dialog mutates the composer's Name + CatalogPrefixes
        // directly). Pin the by-value capture so a future "optimisation" that
        // holds a reference doesn't silently break rename detection.
        var composer = new CanonComposer
        {
            Name = "Beethoven",
            CatalogPrefixes = new List<string> { "Op." },
        };

        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        // Mutate the composer post-snapshot.
        composer.Name = "Mutated";
        composer.CatalogPrefixes.Add("WoO");

        Assert.Equal("Beethoven", snapshot.Name);
        Assert.Equal(new[] { "Op." }, snapshot.CatalogPrefixes);
    }

    [Fact]
    public void CaptureComposerSnapshot_NullPrefixes_ProducesEmptyList()
    {
        var snapshot = CanonViewModel.CaptureComposerSnapshot(
            new CanonComposer { Name = "X", CatalogPrefixes = null });
        Assert.Empty(snapshot.CatalogPrefixes);
    }

    // ── CompleteEditComposerAsync ────────────────────────────────────────────

    [Fact]
    public async Task CompleteEditComposer_NoChanges_SavesOnly_SetsStatus()
    {
        var vm = Build(out var svc, out _);
        var composer = new CanonComposer { Name = "Mozart", SortName = "Mozart" };
        vm.Composers.Add(composer);
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        await vm.CompleteEditComposerAsync(composer, snapshot);

        await svc.Received(1).SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        await svc.DidNotReceive().SavePiecesAsync(Arg.Any<List<CanonPiece>>());
        Assert.Equal("Updated Mozart.", vm.StatusMessage);
    }

    [Fact]
    public async Task CompleteEditComposer_NameChanged_PropagatesRenameToPieces()
    {
        var vm = Build(out var svc, out _);
        var composer = new CanonComposer { Name = "Mozart", SortName = "Mozart" };
        vm.Composers.Add(composer);
        vm.Pieces.Add(new CanonPiece { Composer = "Mozart", Title = "Requiem" });
        vm.Pieces.Add(new CanonPiece { Composer = "Mozart", Title = "Don Giovanni" });
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        // Simulate the editor dialog mutating the composer's name.
        composer.Name = "Mozart, Wolfgang Amadeus";

        await vm.CompleteEditComposerAsync(composer, snapshot);

        // ComposerRenamePropagator updated every piece's Composer field.
        Assert.All(vm.Pieces, p =>
            Assert.Equal("Mozart, Wolfgang Amadeus", p.Composer));
        Assert.Equal("Updated Mozart, Wolfgang Amadeus.", vm.StatusMessage);
    }

    [Fact]
    public async Task CompleteEditComposer_FiresDataMutatedBeforeSave()
    {
        // H36 contract: DataMutated must fire BEFORE the save's await so the
        // View's OnVmDataMutated runs synchronously between mutation and save.
        // Same defence as NewComposerCommand_DataMutatedFiresBeforeSave.
        var vm = Build(out var svc, out _);
        var composer = new CanonComposer { Name = "X", SortName = "X" };
        vm.Composers.Add(composer);
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);
        bool dataMutatedFiredBeforeSave = false;
        bool saveCalled = false;

        vm.DataMutated += () =>
        {
            if (!saveCalled) dataMutatedFiredBeforeSave = true;
        };
        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns(_ => { saveCalled = true; return Task.CompletedTask; });

        await vm.CompleteEditComposerAsync(composer, snapshot);

        Assert.True(dataMutatedFiredBeforeSave);
    }

    [Fact]
    public async Task CompleteEditComposer_NoRename_DoesNotMutatePieces()
    {
        // Reverse coverage: when the Name is unchanged the rename propagator
        // must NOT touch pieces (a defensive write would still produce the
        // right result, but it's wasted work + a chance to bug-break composer
        // case normalisation later).
        var vm = Build(out _, out _);
        var composer = new CanonComposer { Name = "Original Casing", SortName = "X" };
        vm.Composers.Add(composer);
        vm.Pieces.Add(new CanonPiece { Composer = "preserved different casing", Title = "Y" });
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        await vm.CompleteEditComposerAsync(composer, snapshot);

        Assert.Equal("preserved different casing", vm.Pieces[0].Composer);
    }

    // ── CompleteEditPieceAsync ───────────────────────────────────────────────

    [Fact]
    public async Task CompleteEditPiece_SavesViaBatch_FiresDataMutated_SetsStatus()
    {
        // SaveBatch (atomic pieces + pick-lists) — same shape as
        // NewPieceCommand. A future "simplification" reverting to two separate
        // SavePieces + SavePickLists calls reopens the inter-call window.
        var vm = Build(out var svc, out _);
        var piece = new CanonPiece { Composer = "Mozart", Title = "Requiem" };
        vm.Pieces.Add(piece);
        var snapshot = PieceRefPathDiffer.Snapshot(piece);
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.CompleteEditPieceAsync(piece, snapshot);

        Assert.Equal(1, dataMutatedFired);
        await svc.Received(1).SaveBatchAsync(
            null,
            Arg.Is<List<CanonPiece>>(list => list.Contains(piece)),
            null,
            null,
            vm.PickLists);
        await svc.DidNotReceive().SavePiecesAsync(Arg.Any<List<CanonPiece>>());
        await svc.DidNotReceive().SavePickListsAsync(Arg.Any<CanonPickLists>());
        Assert.Contains("Updated piece:", vm.StatusMessage);
    }

    // ── CompleteEditVersionAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CompleteEditVersion_SavesViaBatch_FiresDataMutated_SetsStatus()
    {
        var vm = Build(out var svc, out _);
        var parent = new CanonPiece { Composer = "Liszt", Title = "Sonata" };
        var version = new CanonPieceVersion { Description = "Orchestra arrangement" };
        var versionNode = new VersionDisplayNode(version, parentPiece: parent);
        vm.Pieces.Add(parent);
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.CompleteEditVersionAsync(versionNode);

        Assert.Equal(1, dataMutatedFired);
        await svc.Received(1).SaveBatchAsync(
            null,
            Arg.Any<List<CanonPiece>>(),
            null,
            null,
            vm.PickLists);
        Assert.Equal("Updated version: Orchestra arrangement.", vm.StatusMessage);
    }

    [Fact]
    public async Task CompleteEditVersion_NullDescription_StatusFallsBackToSentinel()
    {
        var vm = Build(out _, out _);
        var parent = new CanonPiece { Composer = "Liszt", Title = "Sonata" };
        var version = new CanonPieceVersion { Description = null };
        var versionNode = new VersionDisplayNode(version, parentPiece: parent);

        await vm.CompleteEditVersionAsync(versionNode);

        Assert.Equal("Updated version: (no description).", vm.StatusMessage);
    }

    // ── CompleteEditSubpieceAsync ────────────────────────────────────────────

    [Fact]
    public async Task CompleteEditSubpiece_SavesViaBatch_FiresDataMutated_SetsStatus()
    {
        var vm = Build(out var svc, out _);
        var subpiece = new CanonPiece { Composer = "Beethoven", Title = "Adagio sostenuto", Number = 1 };
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.CompleteEditSubpieceAsync(subpiece);

        Assert.Equal(1, dataMutatedFired);
        await svc.Received(1).SaveBatchAsync(
            null,
            Arg.Any<List<CanonPiece>>(),
            null,
            null,
            vm.PickLists);
        Assert.StartsWith("Updated:", vm.StatusMessage);
    }
}
