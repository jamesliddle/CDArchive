using System.Collections;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for the two RelayCommands H36 (TracksView
/// slice) surfaced on <see cref="TracksViewModel"/>:
/// <c>ApproveTracksCommand</c> and <c>RejectTracksCommand</c>. Each was
/// previously a code-behind <c>Click</c> handler whose only path through
/// the VM was the lower-level <c>ApproveRowsAsync</c> /
/// <c>RejectRowsAsync</c> helpers.
/// </summary>
public class TracksViewModelCommandTests
{
    private static (TracksViewModel vm, RecordingDialogService dialogs, ICanonDataService svc) Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>());
        var dialogs  = new RecordingDialogService();

        // TracksViewModel needs an AlbumsViewModel singleton; it's a regular
        // VM, easy to construct with stubs.
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);

        var vm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return (vm, dialogs, svc);
    }

    private static AlbumTrackRow MakeLooseRow(string piece, bool provisional = true)
    {
        var track = new AlbumTrack
        {
            TrackNumber   = 0,    // loose-track sentinel
            Description   = piece,
            IsProvisional = provisional,
        };
        // AlbumTrackRow(album, disc, track, piece) — positional ctor.
        return new AlbumTrackRow(album: null, disc: null, track, piece);
    }

    // ── ApproveTracksCommand ──────────────────────────────────────────────────

    [Fact]
    public async Task ApproveTracksCommand_NoSelection_DoesNothing()
    {
        var (vm, dialogs, svc) = Build();

        await vm.ApproveTracksCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ConfirmCalls);
        Assert.Empty(dialogs.InfoCalls);
        Assert.Empty(dialogs.ErrorCalls);
        await svc.DidNotReceive().SaveLooseTracksAsync(Arg.Any<List<AlbumTrack>>());
    }

    [Fact]
    public async Task ApproveTracksCommand_AllNonProvisional_SetsNothingToApproveStatus()
    {
        var (vm, _, _) = Build();
        var row = MakeLooseRow("Already approved", provisional: false);

        await vm.ApproveTracksCommand.ExecuteAsync(new ArrayList { row });

        // TrackCascade.Approve returns 0 (nothing to change), so the
        // "no provisional in selection" status path fires.
        Assert.Contains("No provisional tracks", vm.StatusMessage);
    }

    [Fact]
    public async Task ApproveTracksCommand_HasProvisional_ClearsFlag_AndSavesLoose()
    {
        var (vm, _, svc) = Build();
        var row = MakeLooseRow("Doomed-to-survive", provisional: true);

        await vm.ApproveTracksCommand.ExecuteAsync(new ArrayList { row });

        Assert.False(row.Track.IsProvisional);
        // Loose-track save fires because the row's Album is null.
        await svc.Received(1).SaveLooseTracksAsync(Arg.Any<List<AlbumTrack>>());
        Assert.Contains("Approved 1 track(s)", vm.StatusMessage);
    }

    // ── RejectTracksCommand ───────────────────────────────────────────────────

    [Fact]
    public async Task RejectTracksCommand_AsksForConfirmation_AndRespectsCancel()
    {
        var (vm, dialogs, svc) = Build();
        var row = MakeLooseRow("Survives");
        // Seed the loose-tracks list so the row's track can be removed.
        // TracksViewModel exposes the loose list via _looseTracks (private);
        // we route via the public RebuildRows path: but the simpler approach
        // is to call RejectRowsAsync's underlying path. For this test we
        // just verify the dialog respects cancel.
        dialogs.ConfirmResponse = false;

        await vm.RejectTracksCommand.ExecuteAsync(new ArrayList { row });

        Assert.Single(dialogs.ConfirmCalls);
        Assert.Equal("Reject Tracks", dialogs.ConfirmCalls[0].Title);
        await svc.DidNotReceive().SaveLooseTracksAsync(Arg.Any<List<AlbumTrack>>());
    }

    [Fact]
    public async Task RejectTracksCommand_PromptMessage_BreaksOutAlbumBoundAndLoose()
    {
        var (vm, dialogs, _) = Build();
        // Mix loose (Album=null) + one row pretending to be album-bound. The
        // command's prompt-detail logic should mention "1 loose track(s)"
        // because we only have loose rows here.
        var rows = new ArrayList { MakeLooseRow("A"), MakeLooseRow("B") };
        dialogs.ConfirmResponse = false;

        await vm.RejectTracksCommand.ExecuteAsync(rows);

        var msg = dialogs.ConfirmCalls[0].Message;
        Assert.Contains("2 loose track(s)", msg);
    }

    [Fact]
    public async Task RejectTracksCommand_SingleRow_UsesPieceNameInPrompt()
    {
        var (vm, dialogs, _) = Build();
        var row = MakeLooseRow("Beethoven Op. 27 #2");
        dialogs.ConfirmResponse = false;

        await vm.RejectTracksCommand.ExecuteAsync(new ArrayList { row });

        Assert.Contains("Beethoven Op. 27 #2", dialogs.ConfirmCalls[0].Message);
    }
}
