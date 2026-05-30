using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for the two new RelayCommands H2 slice 4
/// surfaced on <see cref="CanonViewModel"/>: <c>NewComposerCommand</c> and
/// <c>NewPieceCommand</c>. Pre-slice each was a code-behind <c>Click</c>
/// handler with embedded VM-mutation + save orchestration; testing required
/// spinning up WPF. The new commands take a dialog-built target, mutate the
/// VM's observable collections, fire <c>DataMutated</c>, persist, and set
/// status — all headless-testable.
///
/// <para>The modal-dialog launch + <c>ShowDialogWithExpansionGuard</c> stay
/// in the View by design; that path is smoke-tested.</para>
/// </summary>
public class CanonViewModelNewCommandTests
{
    private static CanonViewModel Build(out RecordingDialogService dialogs, out ICanonDataService svc)
    {
        svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>());
        dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    // ── NewComposerCommand ───────────────────────────────────────────────────

    [Fact]
    public async Task NewComposerCommand_NullTarget_NoOp_NoSave()
    {
        var vm = Build(out _, out var svc);

        await vm.NewComposerCommand.ExecuteAsync(null);

        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        Assert.Empty(vm.Composers);
    }

    [Fact]
    public async Task NewComposerCommand_AddsToCollection_FiresDataMutated_Saves_SetsStatus()
    {
        var vm = Build(out _, out var svc);
        var composer = new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven" };
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.NewComposerCommand.ExecuteAsync(composer);

        Assert.Contains(composer, vm.Composers);
        Assert.Equal(1, dataMutatedFired);    // fired once, before the save
        await svc.Received(1).SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        Assert.Equal("Added Beethoven, Ludwig van.", vm.StatusMessage);
    }

    [Fact]
    public async Task NewComposerCommand_DataMutatedFiresBeforeSave()
    {
        // H36-era ordering contract: DataMutated must fire BEFORE the save's
        // await so CanonView's OnVmDataMutated runs synchronously between the
        // collection mutation and the save commencing. Pre-fix order in
        // OnContextApprove (mutate → suppress → await save → rebuild) caused
        // a WPF rendering glitch where expander triangles partly disappeared
        // across the await; the contract here defends against re-introducing
        // that bug in any new command.
        var vm = Build(out _, out var svc);
        var composer = new CanonComposer { Name = "Mozart" };
        bool saveSawComposerInCollection = false;
        bool dataMutatedSawComposerInCollection = false;

        vm.DataMutated += () => dataMutatedSawComposerInCollection = vm.Composers.Contains(composer);
        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns(_ => { saveSawComposerInCollection = vm.Composers.Contains(composer); return Task.CompletedTask; });

        await vm.NewComposerCommand.ExecuteAsync(composer);

        Assert.True(dataMutatedSawComposerInCollection);
        Assert.True(saveSawComposerInCollection);
    }

    [Fact]
    public async Task NewComposerCommand_SaveThrows_RollsBack_ShowsError_DoesNotCrash()
    {
        // Generic save-throws rollback contract — mirror of
        // DeleteComposerCommand's FK-Restrict rollback test. Live failure
        // modes for NewComposer at the SQLite layer: disk full, lock
        // contention, schema-migration mid-flight, etc. The duplicate-Name
        // case is now handled by a pre-save check (see DuplicateName tests
        // below) and never reaches the save path.
        var vm = Build(out var dialogs, out var svc);
        var composer = new CanonComposer { Name = "UniqueName", SortName = "UniqueName" };

        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns<Task>(_ => throw new InvalidOperationException("disk full"));

        // Must not throw — the catch path rolls back and shows an error.
        await vm.NewComposerCommand.ExecuteAsync(composer);

        Assert.DoesNotContain(composer, vm.Composers);
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("disk full", dialogs.ErrorCalls[0].Message);
        Assert.Contains("Add cancelled", vm.StatusMessage);
    }

    [Fact]
    public async Task NewComposerCommand_DuplicateName_RejectedBeforeSave_NoMutation()
    {
        // User-reported regression: a second composer with the same Name as
        // an existing one silently upserted into the existing row at the
        // SQLite layer (the save's byName merge), leaving the in-memory
        // collection with two entries pointing at one DB row. The pre-save
        // duplicate check rejects the duplicate upfront, so neither the
        // collection nor the DB sees the change.
        var vm = Build(out var dialogs, out var svc);
        var existing = new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven" };
        vm.Composers.Add(existing);

        var duplicate = new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven" };

        await vm.NewComposerCommand.ExecuteAsync(duplicate);

        // Duplicate never reached the collection — the existing instance is
        // the only one present.
        Assert.Single(vm.Composers);
        Assert.Same(existing, vm.Composers[0]);

        // Save was never called — the rejection happens before any mutation.
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());

        // User sees a friendly error dialog and a helpful status message.
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("already exists", dialogs.ErrorCalls[0].Message);
        Assert.Equal("Cannot add composer", dialogs.ErrorCalls[0].Title);
        Assert.Contains("Add cancelled", vm.StatusMessage);
        Assert.Contains("Beethoven, Ludwig van", vm.StatusMessage);
    }

    [Fact]
    public async Task NewComposerCommand_DuplicateName_CaseInsensitive()
    {
        // Composer names are matched case-insensitively to mirror the
        // SqliteCanonDataService's byName dictionary (StringComparer.
        // OrdinalIgnoreCase). A different-case duplicate would silently
        // upsert at the DB layer same as an exact-case duplicate.
        var vm = Build(out var dialogs, out var svc);
        vm.Composers.Add(new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven" });

        await vm.NewComposerCommand.ExecuteAsync(
            new CanonComposer { Name = "BEETHOVEN, LUDWIG VAN", SortName = "Beethoven" });

        Assert.Single(vm.Composers);
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        Assert.Single(dialogs.ErrorCalls);
    }

    [Fact]
    public async Task NewComposerCommand_SaveThrows_FiresDataMutatedAgain_ForViewToRefresh()
    {
        // The rollback path needs to fire DataMutated a second time so the
        // View re-renders the tree without the (now-removed) composer.
        // Pre-fix the View's tree would have shown the orphan composer until
        // the next manual refresh.
        var vm = Build(out _, out var svc);
        var composer = new CanonComposer { Name = "X", SortName = "X" };
        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns<Task>(_ => throw new InvalidOperationException("oops"));
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.NewComposerCommand.ExecuteAsync(composer);

        Assert.Equal(2, dataMutatedFired);    // once before save, once for rollback
    }

    // ── NewPieceCommand ──────────────────────────────────────────────────────

    [Fact]
    public async Task NewPieceCommand_NullTarget_NoOp_NoSave()
    {
        var vm = Build(out _, out var svc);

        await vm.NewPieceCommand.ExecuteAsync(null);

        await svc.DidNotReceive().SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(),
            Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(),
            Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
        Assert.Empty(vm.Pieces);
    }

    [Fact]
    public async Task NewPieceCommand_AddsToCollection_FiresDataMutated_Saves_SetsStatus()
    {
        var vm = Build(out _, out var svc);
        var piece = new CanonPiece { Composer = "Beethoven", Title = "Sonata #14" };
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.NewPieceCommand.ExecuteAsync(piece);

        Assert.Contains(piece, vm.Pieces);
        Assert.Equal(1, dataMutatedFired);
        await svc.Received(1).SaveBatchAsync(
            null,
            Arg.Is<List<CanonPiece>>(list => list.Contains(piece)),
            null,
            null,
            vm.PickLists);
        Assert.Contains("Added new piece:", vm.StatusMessage);
    }

    [Fact]
    public async Task NewPieceCommand_UsesSaveBatch_ForAtomicPiecesPlusPickListsWrite()
    {
        // The pre-fix code called SavePiecesCommand then SavePickListsCommand
        // — two separate transactions with a window between them where a
        // crash could leave a piece referencing a not-yet-persisted pick-list
        // value. SaveBatch closes that window. Explicit test pins that the
        // command uses SaveBatch (not SavePiecesAsync / SavePickListsAsync
        // separately) — a future refactor that "simplifies" by reverting to
        // the two-call sequence fails this test.
        var vm = Build(out _, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "P" };

        await vm.NewPieceCommand.ExecuteAsync(piece);

        await svc.Received(1).SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(),
            Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(),
            Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
        await svc.DidNotReceive().SavePiecesAsync(Arg.Any<List<CanonPiece>>());
        await svc.DidNotReceive().SavePickListsAsync(Arg.Any<CanonPickLists>());
    }

    [Fact]
    public async Task NewPieceCommand_SaveThrows_RollsBack_ShowsError_DoesNotCrash()
    {
        var vm = Build(out var dialogs, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "Sonata #99" };

        svc.SaveBatchAsync(
                Arg.Any<List<CanonComposer>?>(),
                Arg.Any<List<CanonPiece>?>(),
                Arg.Any<List<CanonAlbum>?>(),
                Arg.Any<List<AlbumTrack>?>(),
                Arg.Any<CanonPickLists?>())
            .Returns<Task>(_ => throw new InvalidOperationException("constraint violation"));

        await vm.NewPieceCommand.ExecuteAsync(piece);

        Assert.DoesNotContain(piece, vm.Pieces);
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("Add cancelled", vm.StatusMessage);
    }
}
