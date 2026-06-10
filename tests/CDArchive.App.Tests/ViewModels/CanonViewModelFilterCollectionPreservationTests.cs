using System.Collections.ObjectModel;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// M7 regression tests: pin that the per-keystroke filter paths preserve the
/// <c>FilteredComposers</c> / <c>FilteredPieces</c> collection instances
/// across calls. The original symptom was a scroll-reset flicker caused by
/// re-assigning the property with a new <c>ObservableCollection</c>, which
/// forces WPF's <c>ItemsControl</c> to re-virtualise every row. The fix
/// reset the existing instance in place.
/// </summary>
public class CanonViewModelFilterCollectionPreservationTests
{
    private static CanonViewModel Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
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
    public void ApplyComposerFilter_PreservesFilteredComposersInstance()
    {
        var vm = Build();
        vm.Composers.Add(new CanonComposer { Name = "Beethoven", SortName = "Beethoven" });
        vm.Composers.Add(new CanonComposer { Name = "Mozart",    SortName = "Mozart"    });

        vm.ComposerFilter = "Bee";                   // triggers ApplyComposerFilter
        var instanceAfterFirst = vm.FilteredComposers;

        vm.ComposerFilter = "Moz";                   // triggers it again
        var instanceAfterSecond = vm.FilteredComposers;

        Assert.Same(instanceAfterFirst, instanceAfterSecond);
        // Content updated correctly too — proves we reset (not no-op).
        Assert.Single(vm.FilteredComposers);
        Assert.Equal("Mozart", vm.FilteredComposers[0].Name);
    }

    [Fact]
    public void ApplyPiecesFilter_PreservesFilteredPiecesInstance()
    {
        var vm = Build();
        vm.Pieces.Add(new CanonPiece { Composer = "Beethoven", Title = "Sonata #14" });
        vm.Pieces.Add(new CanonPiece { Composer = "Mozart",    Title = "Requiem"    });

        vm.PiecesFilter = "Sonata";
        var instanceAfterFirst = vm.FilteredPieces;

        vm.PiecesFilter = "Requiem";
        var instanceAfterSecond = vm.FilteredPieces;

        Assert.Same(instanceAfterFirst, instanceAfterSecond);
        Assert.Single(vm.FilteredPieces);
        Assert.Equal("Requiem", vm.FilteredPieces[0].Title);
    }

    [Fact]
    public void FilteredComposers_StaysSameInstance_AcrossEmptyFilterTransitions()
    {
        // Edge case: filter goes empty → populated → empty. The collection
        // instance must survive every transition.
        var vm = Build();
        vm.Composers.Add(new CanonComposer { Name = "X", SortName = "X" });
        var originalInstance = vm.FilteredComposers;

        vm.ComposerFilter = "x";
        vm.ComposerFilter = "";
        vm.ComposerFilter = "x";

        Assert.Same(originalInstance, vm.FilteredComposers);
    }
}
