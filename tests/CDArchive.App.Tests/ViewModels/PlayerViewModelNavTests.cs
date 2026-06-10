using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Previous/next-track navigation: <see cref="PlayerViewModel.HasPreviousTrack"/>
/// / <see cref="PlayerViewModel.HasNextTrack"/> gate the prev/next transport
/// buttons, and the commands move through the album sequence.
/// </summary>
public class PlayerViewModelNavTests
{
    private static (PlayerViewModel vm, CanonAlbum album) BuildWithAlbum(int trackCount)
    {
        var locator = Substitute.For<IArchiveAudioLocator>();
        // Every track resolves to a (fake) file so playback "succeeds".
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));

        var vm = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            locator,
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        for (int i = 1; i <= trackCount; i++)
            disc.Tracks.Add(new AlbumTrack { TrackNumber = i });
        var album = new CanonAlbum { Title = "Test Album", Discs = { disc } };

        return (vm, album);
    }

    [Fact]
    public void PlayAlbum_FirstTrack_PrevDisabled_NextEnabled()
    {
        var (vm, album) = BuildWithAlbum(3);

        Assert.Equal(PlayRequestResult.Playing, vm.PlayAlbum(album));

        Assert.False(vm.HasPreviousTrack);
        Assert.True(vm.HasNextTrack);
    }

    [Fact]
    public void NextAndPrevious_WalkTheSequence_AndToggleEnabledState()
    {
        var (vm, album) = BuildWithAlbum(3);
        vm.PlayAlbum(album);

        vm.NextTrackCommand.Execute(null);           // -> track 2
        Assert.True(vm.HasPreviousTrack);
        Assert.True(vm.HasNextTrack);

        vm.NextTrackCommand.Execute(null);           // -> track 3 (last)
        Assert.True(vm.HasPreviousTrack);
        Assert.False(vm.HasNextTrack);

        vm.PreviousTrackCommand.Execute(null);       // -> track 2
        Assert.True(vm.HasPreviousTrack);
        Assert.True(vm.HasNextTrack);
    }

    [Fact]
    public void SingleTrackAlbum_BothDisabled()
    {
        var (vm, album) = BuildWithAlbum(1);
        vm.PlayAlbum(album);

        Assert.False(vm.HasPreviousTrack);
        Assert.False(vm.HasNextTrack);
    }

    [Fact]
    public void PlaySingleTrack_DisablesBoth_NoAutoAdvanceContext()
    {
        var (vm, album) = BuildWithAlbum(3);
        var disc = album.Discs[0];

        Assert.Equal(PlayRequestResult.Playing,
            vm.PlaySingleTrack(album, disc, disc.Tracks[0]));

        // Single-track audition wipes the album context → no prev/next.
        Assert.False(vm.HasPreviousTrack);
        Assert.False(vm.HasNextTrack);
    }

    // ── Gapless auto-advance ─────────────────────────────────────────────────

    [Fact]
    public void PlayAlbum_QueuesTheNextTrack_ForGaplessAdvance()
    {
        var player = Substitute.For<IAudioPlayerService>();
        player.QueueNext(Arg.Any<string?>()).Returns(true);
        var locator = Substitute.For<IArchiveAudioLocator>();
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));
        var vm = new PlayerViewModel(player, locator, Substitute.For<IArchiveSettings>(),
                                     Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 1 });
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 2 });
        vm.PlayAlbum(new CanonAlbum { Title = "A", Discs = { disc } });

        // Starting track 1 pre-loads track 2 into the engine.
        player.Received().QueueNext("x.flac");
    }

    [Fact]
    public void TrackTransitioned_AdvancesContext_AndQueuesTheFollowingTrack()
    {
        var player = Substitute.For<IAudioPlayerService>();
        player.QueueNext(Arg.Any<string?>()).Returns(true);
        var locator = Substitute.For<IArchiveAudioLocator>();
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));
        var vm = new PlayerViewModel(player, locator, Substitute.For<IArchiveSettings>(),
                                     Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        for (int i = 1; i <= 3; i++) disc.Tracks.Add(new AlbumTrack { TrackNumber = i });
        vm.PlayAlbum(new CanonAlbum { Title = "A", Discs = { disc } });

        // Engine reports a gapless hand-off to the queued track 2.
        player.TrackTransitioned += Raise.Event<EventHandler>(player, EventArgs.Empty);
        Assert.True(vm.HasPreviousTrack);
        Assert.True(vm.HasNextTrack);

        // ...and again to track 3 (last).
        player.TrackTransitioned += Raise.Event<EventHandler>(player, EventArgs.Empty);
        Assert.True(vm.HasPreviousTrack);
        Assert.False(vm.HasNextTrack);
    }

    [Fact]
    public void StopAfterCurrent_DoesNotQueueANextTrack()
    {
        var player = Substitute.For<IAudioPlayerService>();
        player.QueueNext(Arg.Any<string?>()).Returns(true);
        var settings = Substitute.For<IArchiveSettings>();
        settings.StopAfterCurrentTrack.Returns(true);
        var locator = Substitute.For<IArchiveAudioLocator>();
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));
        var vm = new PlayerViewModel(player, locator, settings, Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 1 });
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 2 });
        vm.PlayAlbum(new CanonAlbum { Title = "A", Discs = { disc } });

        // Stop-after-current → the queue is cleared, never set to a real track.
        player.DidNotReceive().QueueNext("x.flac");
        player.Received().QueueNext(null);
    }

    [Fact]
    public void SeekSeconds_ReadFromSettings_Clamped()
    {
        var locator = Substitute.For<IArchiveAudioLocator>();
        var settings = Substitute.For<IArchiveSettings>();
        settings.SeekBackwardSeconds.Returns(15);
        settings.SeekForwardSeconds.Returns(30);
        var vm = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(), locator, settings,
            Substitute.For<ICanonDataService>());

        Assert.Equal(15, vm.SeekBackSeconds);
        Assert.Equal(30, vm.SeekForwardSeconds);
    }

    [Fact]
    public void Previous_PastThreshold_RestartsCurrentTrack()
    {
        var player = Substitute.For<IAudioPlayerService>();
        player.Position.Returns(TimeSpan.FromSeconds(5)); // past the threshold
        var settings = Substitute.For<IArchiveSettings>();
        settings.PreviousRestartThresholdSeconds.Returns(2);

        var locator = Substitute.For<IArchiveAudioLocator>();
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));
        var vm = new PlayerViewModel(player, locator, settings, Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 1 });
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 2 });
        vm.PlayAlbum(new CanonAlbum { Title = "A", Discs = { disc } });
        vm.NextTrackCommand.Execute(null); // now on track 2, HasPreviousTrack = true
        player.ClearReceivedCalls();

        vm.PreviousTrackCommand.Execute(null);

        // Past the threshold → restart, NOT skip to the previous track.
        player.Received().Seek(TimeSpan.Zero);
    }

    [Fact]
    public void Previous_BeforeThreshold_GoesToPreviousTrack()
    {
        var player = Substitute.For<IAudioPlayerService>();
        player.Position.Returns(TimeSpan.FromSeconds(1)); // before the threshold
        var settings = Substitute.For<IArchiveSettings>();
        settings.PreviousRestartThresholdSeconds.Returns(2);

        var locator = Substitute.For<IArchiveAudioLocator>();
        locator.Resolve(Arg.Any<CanonAlbum>(), Arg.Any<AlbumDisc>(), Arg.Any<AlbumTrack>())
               .Returns(new AudioFileLocation("x.flac", AudioFormat.Flac));
        var vm = new PlayerViewModel(player, locator, settings, Substitute.For<ICanonDataService>());

        var disc = new AlbumDisc { DiscNumber = 1 };
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 1 });
        disc.Tracks.Add(new AlbumTrack { TrackNumber = 2 });
        vm.PlayAlbum(new CanonAlbum { Title = "A", Discs = { disc } });
        vm.NextTrackCommand.Execute(null); // on track 2 → HasPrev true, HasNext false

        vm.PreviousTrackCommand.Execute(null);

        // Before the threshold → skip back to track 1.
        Assert.False(vm.HasPreviousTrack);
        Assert.True(vm.HasNextTrack);
    }
}
