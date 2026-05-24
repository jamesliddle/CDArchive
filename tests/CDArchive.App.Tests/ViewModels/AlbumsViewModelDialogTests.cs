using CDArchive.App.Services;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for <see cref="AlbumsViewModel"/>'s dialog
/// interaction (Reject confirmation, H3). Locks in: the VM asks for
/// confirmation before destructive action, and respects the user's
/// answer.
///
/// <para>First file under the new <c>CDArchive.App.Tests</c> project —
/// see H39 retirement note for the broader pattern.</para>
/// </summary>
public class AlbumsViewModelDialogTests
{
    /// <summary>
    /// Build an <see cref="AlbumsViewModel"/> wired up with a recording
    /// dialog double and substituted Core services. Tests can then inject
    /// the album list via <see cref="AlbumsViewModel.LoadDataAsync"/>'s
    /// underlying state by calling <see cref="SeedAlbums"/>.
    /// </summary>
    private static (AlbumsViewModel vm, RecordingDialogService dialogs, ICanonDataService svc) Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        // PlayerViewModel needs three services to construct; we only need
        // a non-null reference for the AlbumsViewModel ctor.
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>());
        var dialogs  = new RecordingDialogService();

        var vm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        return (vm, dialogs, svc);
    }

    private static void SeedAlbums(AlbumsViewModel vm, params CanonAlbum[] albums)
    {
        // AllAlbums is the underlying mutable list; expose it via the
        // public accessor and append rather than reassigning the field.
        var list = vm.AllAlbums;
        foreach (var a in albums) list.Add(a);
    }

    [Fact]
    public async Task RejectAlbumAsync_AsksForConfirmation_BeforeDeleting()
    {
        var (vm, dialogs, _) = Build();
        var album = new CanonAlbum { Title = "Test Album", IsProvisional = true };
        SeedAlbums(vm, album);

        await vm.RejectAlbumAsync(album);

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains("Test Album", dialogs.ConfirmCalls[0].Message);
        Assert.Equal("Confirm Rejection", dialogs.ConfirmCalls[0].Title);
    }

    [Fact]
    public async Task RejectAlbumAsync_WhenUserCancels_LeavesAlbumInList_AndDoesNotSave()
    {
        var (vm, dialogs, svc) = Build();
        var album = new CanonAlbum { Title = "Survives", IsProvisional = true };
        SeedAlbums(vm, album);
        dialogs.ConfirmResponse = false;  // user clicks Cancel

        await vm.RejectAlbumAsync(album);

        Assert.Contains(album, vm.AllAlbums);
        await svc.DidNotReceive().SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
    }

    [Fact]
    public async Task RejectAlbumAsync_WhenUserConfirms_RemovesAlbumAndSaves()
    {
        var (vm, dialogs, svc) = Build();
        var album = new CanonAlbum { Title = "Doomed", IsProvisional = true };
        SeedAlbums(vm, album);
        dialogs.ConfirmResponse = true;  // user clicks OK

        await vm.RejectAlbumAsync(album);

        Assert.DoesNotContain(album, vm.AllAlbums);
        await svc.Received(1).SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
        Assert.Contains("Doomed", vm.StatusMessage);
    }
}
