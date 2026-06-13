using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Slice 4 of feature/variants: TracksView flags rows whose piece defines
/// variants but identifies none, and the "Needs variant" filter narrows to
/// exactly those rows. Exercises <see cref="AlbumTrackRow.NeedsVariantIdentification"/>
/// (computed at row build via the resolver) and
/// <see cref="TracksViewModel.OnlyNeedsVariant"/>.
/// </summary>
public class TracksViewModelVariantFilterTests
{
    private static PlayerViewModel Player() => new(
        Substitute.For<IAudioPlayerService>(),
        Substitute.For<IArchiveAudioLocator>(),
        Substitute.For<IArchiveSettings>(),
        Substitute.For<ICanonDataService>());

    [Fact]
    public void RebuildRows_FlagsUnchosenVariantRefs_AndFilterNarrows()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = Player();
        var dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);

        // Canon: one piece WITH a variant, one WITHOUT.
        var withVariant = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo> { new() { Description = "Kreisler cadenza" } },
        };
        var plain = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Egmont Overture",
        };
        refIndex.BuildResolver(new List<CanonPiece> { withVariant, plain });

        // Album: track 1 refs the variant-bearing piece with NO variant chosen;
        // track 2 refs the plain piece.
        albumsVm.AllAlbums.Add(new CanonAlbum
        {
            Title = "Beethoven recital",
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new()
                        {
                            TrackNumber = 1,
                            PieceRefs = new List<TrackPieceRef>
                            {
                                new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Violin Concerto" },
                            },
                        },
                        new()
                        {
                            TrackNumber = 2,
                            PieceRefs = new List<TrackPieceRef>
                            {
                                new() { Composer = "Beethoven, Ludwig van", PieceTitle = "Egmont Overture" },
                            },
                        },
                    },
                },
            },
        });

        var vm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        vm.RebuildRows();
        vm.ApplyFilter();

        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.Rows.Single(r => r.Track.TrackNumber == 1).NeedsVariantIdentification);
        Assert.False(vm.Rows.Single(r => r.Track.TrackNumber == 2).NeedsVariantIdentification);

        // Toggling the filter narrows to the row that needs a variant.
        vm.OnlyNeedsVariant = true;
        Assert.Single(vm.Rows);
        Assert.Equal(1, vm.Rows[0].Track.TrackNumber);

        // Clearing it restores both.
        vm.OnlyNeedsVariant = false;
        Assert.Equal(2, vm.Rows.Count);
    }

    [Fact]
    public void RefWithChosenVariant_NotFlagged()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = Player();
        var dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);

        var withVariant = new CanonPiece
        {
            Composer = "Beethoven, Ludwig van",
            Title    = "Violin Concerto",
            Variants = new List<VariantInfo> { new() { Description = "Kreisler cadenza" } },
        };
        refIndex.BuildResolver(new List<CanonPiece> { withVariant });

        albumsVm.AllAlbums.Add(new CanonAlbum
        {
            Title = "VC with cadenza picked",
            Discs = new List<AlbumDisc>
            {
                new()
                {
                    DiscNumber = 1,
                    Tracks = new List<AlbumTrack>
                    {
                        new()
                        {
                            TrackNumber = 1,
                            PieceRefs = new List<TrackPieceRef>
                            {
                                new()
                                {
                                    Composer   = "Beethoven, Ludwig van",
                                    PieceTitle = "Violin Concerto",
                                    Variants   = new List<VariantReference>
                                    {
                                        new() { Description = "Kreisler cadenza" },
                                    },
                                },
                            },
                        },
                    },
                },
            },
        });

        var vm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        vm.RebuildRows();
        vm.ApplyFilter();

        Assert.False(vm.Rows.Single().NeedsVariantIdentification);
    }
}
