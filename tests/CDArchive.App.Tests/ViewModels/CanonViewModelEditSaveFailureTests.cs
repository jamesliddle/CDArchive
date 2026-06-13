using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Regression for the variant-delete crash: removing a variant (or subpiece)
/// that an album-track recording still references makes the piece save throw a
/// friendly <see cref="InvalidOperationException"/> from the data layer. The
/// edit-completion flows must catch it and surface a clean dialog rather than
/// letting it escape to the global unhandled-exception handler (which showed a
/// scary stack-trace dialog). On failure the canon is reloaded so the rejected
/// in-memory edit is discarded.
/// </summary>
public class CanonViewModelEditSaveFailureTests
{
    private const string VariantRejection =
        "Cannot delete 1 variant(s) ('A-sharp in bars 224–226'…) — they are still " +
        "identified on one or more album track recordings. Clear those variant selections first.";

    private static CanonViewModel Build(out RecordingDialogService dialogs, out ICanonDataService svc)
    {
        svc = Substitute.For<ICanonDataService>();
        // Loads used by the post-failure reload — return empty so LoadDataAsync
        // completes cleanly.
        svc.LoadComposersAsync().Returns(new List<CanonComposer>());
        svc.LoadPiecesAsync().Returns(new List<CanonPiece>());
        svc.LoadPickListsAsync().Returns(new CanonPickLists());
        svc.LoadAlbumsAsync().Returns(new List<CanonAlbum>());
        svc.LoadLooseTracksAsync().Returns(new List<AlbumTrack>());

        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());
        dialogs = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    private static void MakeSaveBatchThrow(ICanonDataService svc) =>
        svc.SaveBatchAsync(
                Arg.Any<List<CanonComposer>?>(),
                Arg.Any<List<CanonPiece>?>(),
                Arg.Any<List<CanonAlbum>?>(),
                Arg.Any<List<AlbumTrack>?>(),
                Arg.Any<CanonPickLists?>())
            .ThrowsAsync(new InvalidOperationException(VariantRejection));

    [Fact]
    public async Task CompleteEditSubpiece_RejectedSave_ShowsFriendlyDialog_DoesNotThrow()
    {
        var vm = Build(out var dialogs, out var svc);
        MakeSaveBatchThrow(svc);

        var subpiece = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Rondo" };

        // Must NOT throw — pre-fix this propagated to the global handler.
        await vm.CompleteEditSubpieceAsync(subpiece);

        var error = Assert.Single(dialogs.ErrorCalls);
        Assert.Contains("variant", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("A-sharp in bars 224", error.Message);
        // Reload was attempted to restore DB truth.
        await svc.Received().LoadPiecesAsync();
        Assert.Contains("not saved", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteEditPiece_RejectedSave_ShowsFriendlyDialog_DoesNotThrow()
    {
        var vm = Build(out var dialogs, out var svc);
        MakeSaveBatchThrow(svc);

        var piece = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Violin Concerto" };
        var snapshot = CDArchive.Core.Helpers.PieceRefPathDiffer.Snapshot(piece);

        await vm.CompleteEditPieceAsync(piece, snapshot);

        Assert.Single(dialogs.ErrorCalls);
        await svc.Received().LoadPiecesAsync();
    }

    [Fact]
    public async Task CompleteEditVersion_RejectedSave_ShowsFriendlyDialog_DoesNotThrow()
    {
        var vm = Build(out var dialogs, out var svc);
        MakeSaveBatchThrow(svc);

        var parent  = new CanonPiece { Composer = "Beethoven, Ludwig van", Title = "Violin Concerto" };
        var version = new CanonPieceVersion { Description = "arr. for piano" };
        var node    = new VersionDisplayNode(version, parentPiece: parent);

        await vm.CompleteEditVersionAsync(node);

        Assert.Single(dialogs.ErrorCalls);
    }
}
