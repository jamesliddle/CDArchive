using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for the four RelayCommands H36 surfaced on
/// <see cref="CanonViewModel"/> for the CanonView Click-handler retirement:
/// <c>DeleteComposerCommand</c>, <c>DeletePieceCommand</c>,
/// <c>ApproveCanonItemCommand</c>, <c>RejectCanonItemCommand</c>.
///
/// <para>Pre-fix each was a code-behind <c>Click</c> handler with embedded
/// <c>MessageBox.Show</c> calls and direct VM mutation; testing required
/// spinning up WPF. The new commands route through
/// <see cref="RecordingDialogService"/> for confirmation chrome and mutate
/// the VM's observable collections + fire <c>DataMutated</c> — all
/// headless-testable.</para>
/// </summary>
public class CanonViewModelCommandTests
{
    private static CanonViewModel Build(out RecordingDialogService dialogs, out ICanonDataService svc)
    {
        svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());
        dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    // ── DeleteComposerCommand ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteComposerCommand_NullTarget_NoOp_NoConfirmation()
    {
        var vm = Build(out var dialogs, out var svc);

        await vm.DeleteComposerCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ConfirmCalls);
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
    }

    [Fact]
    public async Task DeleteComposerCommand_UserCancels_NoMutation_NoSave()
    {
        var vm = Build(out var dialogs, out var svc);
        dialogs.ConfirmResponse = false;
        var composer = new CanonComposer { Name = "Beethoven, Ludwig van" };
        vm.Composers.Add(composer);

        await vm.DeleteComposerCommand.ExecuteAsync(composer);

        Assert.Single(dialogs.ConfirmCalls);                                  // confirmation shown
        Assert.Contains(composer, vm.Composers);                              // unchanged
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
    }

    [Fact]
    public async Task DeleteComposerCommand_UserConfirms_RemovesAndSaves_FiresDataMutated()
    {
        var vm = Build(out var dialogs, out var svc);
        var composer = new CanonComposer { Name = "Mozart" };
        vm.Composers.Add(composer);
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.DeleteComposerCommand.ExecuteAsync(composer);

        Assert.Single(dialogs.ConfirmCalls);
        Assert.DoesNotContain(composer, vm.Composers);                        // removed
        Assert.Equal(1, dataMutatedFired);                                    // fired
        await svc.Received(1).SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        Assert.Equal("Deleted Mozart.", vm.StatusMessage);
    }

    [Fact]
    public async Task DeleteComposerCommand_SaveThrows_RestoresComposer_ShowsError_DoesNotCrash()
    {
        // User-reported regression: deleting a composer that still owns pieces
        // crashed the app. SaveComposersCoreAsync wraps the SQLite FK Restrict
        // failure in an InvalidOperationException; pre-fix this propagated past
        // the async RelayCommand and crashed the dispatcher.
        var vm = Build(out var dialogs, out var svc);
        var composer = new CanonComposer { Name = "Aatest, Aaron A" };
        vm.Composers.Add(composer);

        // Simulate the FK Restrict path.
        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns<Task>(_ => throw new InvalidOperationException(
                "Cannot delete 1 composer(s) ('Aatest, Aaron A'…) — one or more " +
                "still owns pieces in the canon. Remove their pieces first."));

        // Must not throw — the catch path restores the composer and shows
        // an error dialog instead.
        await vm.DeleteComposerCommand.ExecuteAsync(composer);

        // Composer restored to the in-memory collection so the UI matches DB
        // reality (where the row still exists).
        Assert.Contains(composer, vm.Composers);

        // Error dialog surfaced with the helpful message from SaveComposersCoreAsync.
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("still owns pieces", dialogs.ErrorCalls[0].Message);

        // Status reflects the cancelled operation.
        Assert.Contains("Delete cancelled", vm.StatusMessage);
        Assert.Contains("Aatest, Aaron A",   vm.StatusMessage);
    }

    [Fact]
    public async Task DeleteComposerCommand_ClearsSelectedComposer_WhenTargetMatches()
    {
        var vm = Build(out _, out _);
        var composer = new CanonComposer { Name = "X" };
        vm.Composers.Add(composer);
        vm.SelectedComposer = composer;

        await vm.DeleteComposerCommand.ExecuteAsync(composer);

        Assert.Null(vm.SelectedComposer);
    }

    // ── DeletePieceCommand ───────────────────────────────────────────────────

    [Fact]
    public async Task DeletePieceCommand_UserCancels_NoMutation()
    {
        var vm = Build(out var dialogs, out var svc);
        dialogs.ConfirmResponse = false;
        var piece = new CanonPiece { Composer = "X", Title = "Sonata 14" };
        vm.Pieces.Add(piece);

        await vm.DeletePieceCommand.ExecuteAsync(piece);

        Assert.Contains(piece, vm.Pieces);
        await svc.DidNotReceive().SaveBatchAsync(
            Arg.Any<List<CanonComposer>>(),
            Arg.Any<List<CanonPiece>>(),
            Arg.Any<List<CanonAlbum>?>(),
            Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
    }

    [Fact]
    public async Task DeletePieceCommand_UserConfirms_RemovesAndSavesBatch()
    {
        var vm = Build(out _, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "Sonata 14" };
        vm.Pieces.Add(piece);
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.DeletePieceCommand.ExecuteAsync(piece);

        Assert.DoesNotContain(piece, vm.Pieces);
        Assert.Equal(1, dataMutatedFired);
        // SaveBatch (composers + pieces) is the right shape — pre-fix the View
        // called SaveAllAsync(vm) which routes through SaveBatch.
        await svc.Received(1).SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(),
            Arg.Any<List<CanonPiece>?>(),
            null, null, null);
        Assert.Contains("Sonata 14", vm.StatusMessage);
    }

    [Fact]
    public async Task DeletePieceCommand_SaveThrows_RestoresPiece_ShowsError_DoesNotCrash()
    {
        // Same pattern as the DeleteComposer fix: SaveBatch can throw (e.g.
        // piece still referenced by album track piece-refs). Pre-fix the
        // unhandled async exception crashed the dispatcher.
        var vm = Build(out var dialogs, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "Sonata 14" };
        vm.Pieces.Add(piece);

        svc.SaveBatchAsync(
                Arg.Any<List<CanonComposer>?>(),
                Arg.Any<List<CanonPiece>?>(),
                null, null, null)
            .Returns<Task>(_ => throw new InvalidOperationException("FK Restrict simulated"));

        await vm.DeletePieceCommand.ExecuteAsync(piece);

        Assert.Contains(piece, vm.Pieces);                      // restored
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("Sonata 14", vm.StatusMessage);
        Assert.Contains("Delete cancelled", vm.StatusMessage);
    }

    // ── ApproveCanonItemCommand ──────────────────────────────────────────────

    [Fact]
    public async Task ApproveCanonItem_ProvisionalComposer_FlipsAndSaves()
    {
        var vm = Build(out _, out var svc);
        var composer = new CanonComposer { Name = "X", IsProvisional = true };
        vm.Composers.Add(composer);
        var node = new ComposerTreeNode(composer, Array.Empty<CanonPiece>());
        var dataMutatedFired = 0;
        vm.DataMutated += () => dataMutatedFired++;

        await vm.ApproveCanonItemCommand.ExecuteAsync(node);

        Assert.False(composer.IsProvisional);
        Assert.Equal(1, dataMutatedFired);
        await svc.Received(1).SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        Assert.Equal("Approved X.", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveCanonItem_AlreadyApprovedComposer_NoOp()
    {
        var vm = Build(out _, out var svc);
        var composer = new CanonComposer { Name = "X", IsProvisional = false };
        vm.Composers.Add(composer);
        var node = new ComposerTreeNode(composer, Array.Empty<CanonPiece>());

        await vm.ApproveCanonItemCommand.ExecuteAsync(node);

        // No save because the guard "when node.Composer.IsProvisional" skips
        // already-approved items.
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
    }

    [Fact]
    public async Task ApproveCanonItem_ProvisionalPiece_FlipsAndSaves()
    {
        var vm = Build(out _, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "Sonata", IsProvisional = true };
        vm.Pieces.Add(piece);

        await vm.ApproveCanonItemCommand.ExecuteAsync(piece);

        Assert.False(piece.IsProvisional);
        await svc.Received(1).SavePiecesAsync(Arg.Any<List<CanonPiece>>());
        Assert.Contains("Approved", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveCanonItem_ComposerSaveThrows_RollsBack_ShowsError_DoesNotCrash()
    {
        var vm = Build(out var dialogs, out var svc);
        var composer = new CanonComposer { Name = "X", IsProvisional = true };
        vm.Composers.Add(composer);
        var node = new ComposerTreeNode(composer, Array.Empty<CanonPiece>());

        svc.SaveComposersAsync(Arg.Any<List<CanonComposer>>())
            .Returns<Task>(_ => throw new InvalidOperationException("save failed"));

        await vm.ApproveCanonItemCommand.ExecuteAsync(node);

        Assert.True(composer.IsProvisional);                    // rolled back
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("Approve cancelled", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveCanonItem_PieceSaveThrows_RollsBack_ShowsError_DoesNotCrash()
    {
        var vm = Build(out var dialogs, out var svc);
        var piece = new CanonPiece { Composer = "X", Title = "Sonata", IsProvisional = true };
        vm.Pieces.Add(piece);

        svc.SavePiecesAsync(Arg.Any<List<CanonPiece>>())
            .Returns<Task>(_ => throw new InvalidOperationException("save failed"));

        await vm.ApproveCanonItemCommand.ExecuteAsync(piece);

        Assert.True(piece.IsProvisional);                        // rolled back
        Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("Approve cancelled", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveCanonItem_UnknownType_NoOp()
    {
        var vm = Build(out _, out var svc);

        await vm.ApproveCanonItemCommand.ExecuteAsync("not a tree node");

        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        await svc.DidNotReceive().SavePiecesAsync(Arg.Any<List<CanonPiece>>());
    }

    // ── RejectCanonItemCommand ───────────────────────────────────────────────

    [Fact]
    public async Task RejectCanonItem_Piece_UserCancels_NoCascade()
    {
        var vm = Build(out var dialogs, out var svc);
        dialogs.ConfirmResponse = false;
        var piece = new CanonPiece { Composer = "X", Title = "T", IsProvisional = true };
        vm.Pieces.Add(piece);

        await vm.RejectCanonItemCommand.ExecuteAsync(piece);

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains(piece, vm.Pieces);    // not removed
    }

    [Fact]
    public async Task RejectCanonItem_Composer_ConfirmationPromptMentionsOwnedPieceCount()
    {
        var vm = Build(out var dialogs, out var svc);
        var composer = new CanonComposer { Name = "X", IsProvisional = true };
        vm.Composers.Add(composer);
        vm.Pieces.Add(new CanonPiece { Composer = "X", Title = "P1" });
        vm.Pieces.Add(new CanonPiece { Composer = "X", Title = "P2" });
        vm.Pieces.Add(new CanonPiece { Composer = "Other", Title = "Other piece" });
        dialogs.ConfirmResponse = false;   // bail before actually running cascade

        await vm.RejectCanonItemCommand.ExecuteAsync(new ComposerTreeNode(composer, Array.Empty<CanonPiece>()));

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains("2 pieces", dialogs.ConfirmCalls[0].Message);
    }

    [Fact]
    public async Task RejectCanonItem_Composer_OwnsOnePiece_PromptSaysOnePiece()
    {
        var vm = Build(out var dialogs, out _);
        var composer = new CanonComposer { Name = "Y", IsProvisional = true };
        vm.Composers.Add(composer);
        vm.Pieces.Add(new CanonPiece { Composer = "Y", Title = "Only" });
        dialogs.ConfirmResponse = false;

        await vm.RejectCanonItemCommand.ExecuteAsync(new ComposerTreeNode(composer, Array.Empty<CanonPiece>()));

        Assert.Contains("1 piece", dialogs.ConfirmCalls[0].Message);
        Assert.DoesNotContain("0 piece", dialogs.ConfirmCalls[0].Message);
        Assert.DoesNotContain("pieces", dialogs.ConfirmCalls[0].Message);   // singular
    }

    [Fact]
    public async Task RejectCanonItem_UnknownType_NoOp_NoConfirmation()
    {
        var vm = Build(out var dialogs, out _);
        await vm.RejectCanonItemCommand.ExecuteAsync("not a recognised target");
        Assert.Empty(dialogs.ConfirmCalls);
    }
}
