using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Helpers;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Tests for promoting a piece's Form (e.g. "Sonatina") into the global Forms
/// pick list when the piece or a subpiece/version is added or edited, so the
/// Form becomes available in every Form dropdown. Mirrors the per-composer
/// catalogue-prefix promotion.
/// </summary>
public class CanonViewModelFormPromotionTests
{
    private static CanonViewModel Build(out ICanonDataService svc)
    {
        svc = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());
        var dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    [Fact]
    public async Task NewPiece_NovelForm_AddedToGlobalFormsList()
    {
        var vm = Build(out _);
        vm.PickLists = new CanonPickLists { Forms = { "Sonata", "Symphony" } };

        var piece = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van", Title = "", Form = "Sonatina", KeyTonality = "F",
        };

        await vm.NewPieceCommand.ExecuteAsync(piece);

        Assert.Contains("Sonatina", vm.PickLists.Forms);
    }

    [Fact]
    public async Task NewPiece_NovelSubpieceForm_AlsoPromoted()
    {
        var vm = Build(out _);
        vm.PickLists = new CanonPickLists { Forms = { "Sonata" } };

        var piece = new CanonPiece
        {
            Composer = "X", Form = "Suite",
            Subpieces = new List<CanonPiece>
            {
                new() { Composer = "X", Form = "Gavotte" },
            },
        };

        await vm.NewPieceCommand.ExecuteAsync(piece);

        Assert.Contains("Suite", vm.PickLists.Forms);
        Assert.Contains("Gavotte", vm.PickLists.Forms);
    }

    [Fact]
    public async Task EditPiece_NovelForm_AddedAndSavedViaBatchWithPickLists()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { Forms = { "Sonata" } };
        var piece = new CanonPiece { Composer = "Beethoven, Ludwig van", Form = "Sonatina", KeyTonality = "F" };
        vm.Pieces.Add(piece);
        var snapshot = PieceRefPathDiffer.Snapshot(piece);

        await vm.CompleteEditPieceAsync(piece, snapshot);

        Assert.Contains("Sonatina", vm.PickLists.Forms);
        await svc.Received(1).SaveBatchAsync(
            null, Arg.Any<List<CanonPiece>?>(), null, null, vm.PickLists);
    }

    [Fact]
    public async Task FormAlreadyPresent_CaseInsensitive_NotDuplicated()
    {
        var vm = Build(out _);
        vm.PickLists = new CanonPickLists { Forms = { "Sonatina" } };
        var piece = new CanonPiece { Composer = "X", Form = "sonatina" };  // case variant
        vm.Pieces.Add(piece);

        await vm.CompleteEditPieceAsync(piece, PieceRefPathDiffer.Snapshot(piece));

        Assert.Single(vm.PickLists.Forms);
        Assert.Equal("Sonatina", vm.PickLists.Forms[0]);
    }

    [Fact]
    public async Task EditSubpiece_NovelForm_Promoted()
    {
        var vm = Build(out _);
        vm.PickLists = new CanonPickLists { Forms = { "Sonata" } };
        var subpiece = new CanonPiece { Composer = "X", Form = "Rondo" };

        await vm.CompleteEditSubpieceAsync(subpiece);

        Assert.Contains("Rondo", vm.PickLists.Forms);
    }

    [Fact]
    public async Task NewPiece_SaveFails_RollsBackFormAddition()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { Forms = { "Sonata" } };
        svc.SaveBatchAsync(
                Arg.Any<List<CanonComposer>?>(), Arg.Any<List<CanonPiece>?>(),
                Arg.Any<List<CanonAlbum>?>(), Arg.Any<List<AlbumTrack>?>(),
                Arg.Any<CanonPickLists?>())
            .Returns<Task>(_ => throw new InvalidOperationException("disk full"));

        var piece = new CanonPiece { Composer = "X", Form = "Sonatina" };

        await vm.NewPieceCommand.ExecuteAsync(piece);

        Assert.DoesNotContain(piece, vm.Pieces);
        Assert.DoesNotContain("Sonatina", vm.PickLists.Forms);
        Assert.Equal(new[] { "Sonata" }, vm.PickLists.Forms);
    }
}
