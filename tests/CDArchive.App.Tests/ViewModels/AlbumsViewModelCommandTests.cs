using System.Collections;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for the three RelayCommands H36 surfaced on
/// <see cref="AlbumsViewModel"/>: <c>ApproveAlbumsCommand</c>,
/// <c>RejectAlbumsCommand</c>, and <c>CheckReferencesCommand</c>. Each
/// command was previously a code-behind <c>Click</c> handler — testing
/// them required spinning up WPF. Now they're headless, gated by
/// <see cref="RecordingDialogService"/> for any dialog interaction.
/// </summary>
public class AlbumsViewModelCommandTests
{
    private static (AlbumsViewModel vm, RecordingDialogService dialogs, ICanonDataService svc) Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
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
        var list = vm.AllAlbums;
        foreach (var a in albums) list.Add(a);
    }

    // ── ApproveAlbumsCommand ──────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAlbumsCommand_OnlyFlipsProvisionalRows_AndSavesOnce()
    {
        var (vm, _, svc) = Build();
        var p1 = new CanonAlbum { Title = "Prov 1", IsProvisional = true };
        var p2 = new CanonAlbum { Title = "Prov 2", IsProvisional = true };
        var already = new CanonAlbum { Title = "Already approved", IsProvisional = false };
        SeedAlbums(vm, p1, p2, already);

        // ApproveAlbumsCommand takes IList? (the ListView's SelectedItems in
        // production). Pass a plain ArrayList containing all three; the
        // command should filter to provisional and flip those.
        await vm.ApproveAlbumsCommand.ExecuteAsync(new ArrayList { p1, p2, already });

        Assert.False(p1.IsProvisional);
        Assert.False(p2.IsProvisional);
        Assert.False(already.IsProvisional);  // already false, stays false
        await svc.Received(1).SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
        Assert.Contains("Approved 2 album(s)", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveAlbumsCommand_NoProvisionalInSelection_DoesNothing()
    {
        var (vm, _, svc) = Build();
        var a = new CanonAlbum { Title = "Approved", IsProvisional = false };
        SeedAlbums(vm, a);

        await vm.ApproveAlbumsCommand.ExecuteAsync(new ArrayList { a });

        await svc.DidNotReceive().SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
    }

    [Fact]
    public async Task ApproveAlbumsCommand_NullSelection_DoesNothing()
    {
        var (vm, _, svc) = Build();

        await vm.ApproveAlbumsCommand.ExecuteAsync(null);

        await svc.DidNotReceive().SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
    }

    // ── RejectAlbumsCommand ───────────────────────────────────────────────────

    [Fact]
    public async Task RejectAlbumsCommand_AsksForConfirmation_AndRespectsConfirm()
    {
        var (vm, dialogs, svc) = Build();
        var p1 = new CanonAlbum { Title = "Doomed 1", IsProvisional = true };
        var p2 = new CanonAlbum { Title = "Doomed 2", IsProvisional = true };
        SeedAlbums(vm, p1, p2);
        dialogs.ConfirmResponse = true;

        await vm.RejectAlbumsCommand.ExecuteAsync(new ArrayList { p1, p2 });

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains("2 provisional album(s)", dialogs.ConfirmCalls[0].Message);
        Assert.DoesNotContain(p1, vm.AllAlbums);
        Assert.DoesNotContain(p2, vm.AllAlbums);
        await svc.Received(1).SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
    }

    [Fact]
    public async Task RejectAlbumsCommand_UserCancels_LeavesAlbumsAndDoesNotSave()
    {
        var (vm, dialogs, svc) = Build();
        var p = new CanonAlbum { Title = "Survives", IsProvisional = true };
        SeedAlbums(vm, p);
        dialogs.ConfirmResponse = false;

        await vm.RejectAlbumsCommand.ExecuteAsync(new ArrayList { p });

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains(p, vm.AllAlbums);
        await svc.DidNotReceive().SaveAlbumsAsync(Arg.Any<List<CanonAlbum>>());
    }

    [Fact]
    public async Task RejectAlbumsCommand_SkipsNonProvisionalRowsFromSelection()
    {
        var (vm, dialogs, _) = Build();
        var prov     = new CanonAlbum { Title = "Provisional",     IsProvisional = true };
        var approved = new CanonAlbum { Title = "Already approved", IsProvisional = false };
        SeedAlbums(vm, prov, approved);
        dialogs.ConfirmResponse = true;

        // The context menu's enable-check should already filter, but the
        // VM is defensive — it filters too. The prompt should mention "1"
        // (just the provisional row), not "2".
        await vm.RejectAlbumsCommand.ExecuteAsync(new ArrayList { prov, approved });

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Contains("'Provisional'", dialogs.ConfirmCalls[0].Message);
        Assert.DoesNotContain(prov, vm.AllAlbums);
        Assert.Contains(approved, vm.AllAlbums);  // untouched
    }

    // ── CheckReferencesCommand ────────────────────────────────────────────────

    [Fact]
    public async Task CheckReferencesCommand_NoAlbums_ShowsInfoDialog()
    {
        var (vm, dialogs, _) = Build();
        // No albums seeded.

        await vm.CheckReferencesCommand.ExecuteAsync(null);

        Assert.Single(dialogs.InfoCalls);
        Assert.Equal("Check References", dialogs.InfoCalls[0].Title);
        Assert.Contains("No albums loaded", dialogs.InfoCalls[0].Message);
    }
}
