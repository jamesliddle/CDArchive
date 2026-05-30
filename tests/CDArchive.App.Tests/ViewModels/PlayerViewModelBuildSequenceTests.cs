using CDArchive.App.ViewModels;
using CDArchive.Core.Models;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// M8 regression: pre-fix <c>PlayerViewModel.BuildSequence</c> ordered discs
/// by <c>VolumeNumber ?? 0</c>, which collapsed null-volume discs into the
/// same bucket as a (legitimate but unusual) <c>Vol 0</c>. The practical
/// failure mode: a mixed album with Vol 1 + Vol 2 + an un-numbered bonus
/// disc would play the bonus disc FIRST. The fix puts null volumes last
/// via <c>?? int.MaxValue</c>.
/// </summary>
public class PlayerViewModelBuildSequenceTests
{
    [Fact]
    public void BuildSequence_NullVolumeDiscs_OrderedAfterNumberedVolumes()
    {
        // Album with Disc 1 (no volume), Disc 2 in Vol 1, Disc 3 in Vol 2.
        // Pre-fix this played Disc 1 first because `null ?? 0` < 1 < 2.
        // Post-fix Disc 1 must play LAST because `null ?? int.MaxValue` > 2.
        var album = new CanonAlbum
        {
            Title = "Mixed",
            Discs = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, VolumeNumber = null, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 2, VolumeNumber = 1,    Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 3, VolumeNumber = 2,    Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
            }
        };

        var seq = PlayerViewModel.BuildSequence(album);

        Assert.Equal(3, seq.Count);
        Assert.Equal(2, seq[0].Disc.DiscNumber);   // Vol 1
        Assert.Equal(3, seq[1].Disc.DiscNumber);   // Vol 2
        Assert.Equal(1, seq[2].Disc.DiscNumber);   // null volume — last
    }

    [Fact]
    public void BuildSequence_AllNullVolume_OrdersByDiscNumber()
    {
        // The common case: no volumes at all — ordering must just fall through
        // to DiscNumber. Pre-fix this worked correctly (all `?? 0` equal),
        // post-fix it still works (all `?? int.MaxValue` equal).
        var album = new CanonAlbum
        {
            Title = "Plain",
            Discs = new List<AlbumDisc>
            {
                new() { DiscNumber = 3, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 1, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 2, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
            }
        };

        var seq = PlayerViewModel.BuildSequence(album);

        Assert.Equal(new[] { 1, 2, 3 }, seq.Select(e => e.Disc.DiscNumber));
    }

    [Fact]
    public void BuildSequence_AllNumberedVolumes_OrdersByVolumeThenDisc()
    {
        // Box-set shape: Vol 1 Disc 1, Vol 1 Disc 2, Vol 2 Disc 1, etc.
        // Volumes outrank disc numbers — Vol 1 Disc 2 plays before Vol 2 Disc 1.
        var album = new CanonAlbum
        {
            Title = "Box set",
            Discs = new List<AlbumDisc>
            {
                new() { DiscNumber = 1, VolumeNumber = 2, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 2, VolumeNumber = 1, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
                new() { DiscNumber = 1, VolumeNumber = 1, Tracks = new() { new AlbumTrack { TrackNumber = 1 } } },
            }
        };

        var seq = PlayerViewModel.BuildSequence(album);

        Assert.Equal(3, seq.Count);
        Assert.Equal((1, 1), (seq[0].Disc.VolumeNumber, seq[0].Disc.DiscNumber));
        Assert.Equal((1, 2), (seq[1].Disc.VolumeNumber, seq[1].Disc.DiscNumber));
        Assert.Equal((2, 1), (seq[2].Disc.VolumeNumber, seq[2].Disc.DiscNumber));
    }
}
