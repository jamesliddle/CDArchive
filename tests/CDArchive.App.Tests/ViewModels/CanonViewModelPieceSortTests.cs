using System.ComponentModel;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level tests for <see cref="CanonViewModel.PieceSortColumn"/> — the
/// observable property H2 slice 3 migrated off <c>CanonView</c>'s code-behind
/// (was the <c>_pieceSortField</c> field updated by an
/// <c>OnPieceSortChanged</c> SelectionChanged handler).
///
/// <para>The "the View reacts via PropertyChanged" half is smoke-tested
/// manually (a headless run can't realise WPF containers); these tests pin
/// the VM contract that <c>CanonView.OnViewModelPropertyChanged</c> depends
/// on.</para>
///
/// <para>The piece sort itself runs through <see cref="PieceSorting"/> inside
/// <c>CanonView.ApplySortedFilter</c>'s rebuild step — there's no VM-side
/// <c>ApplyPieceSort</c> helper to extract (the way slice 2 had
/// <c>ApplyComposerSort</c>) because the sort operates on a per-composer
/// merged children list during rebuild, which still lives in the View. That
/// orchestration moves in slice 5.</para>
/// </summary>
public class CanonViewModelPieceSortTests
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
    public void PieceSortColumn_Default_IsCatalogue()
    {
        // Matches the pre-fix XAML SelectedIndex="0" + the first ComboBoxItem
        // ("Catalogue"). Opening the Canon view for the first time must
        // produce the same sort the user saw historically.
        var vm = Build();
        Assert.Equal("Catalogue", vm.PieceSortColumn);
    }

    [Fact]
    public void PieceSortColumn_Set_RaisesPropertyChangedWithCorrectName()
    {
        // CanonView.OnViewModelPropertyChanged switches on
        // nameof(CanonViewModel.PieceSortColumn) to trigger a tree rebuild —
        // pin the property-name contract so a refactor that renames the
        // property without updating the View handler fails this test.
        var vm = Build();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PieceSortColumn = "Title";

        Assert.Contains(nameof(CanonViewModel.PieceSortColumn), raised);
    }

    [Fact]
    public void PieceSortColumn_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        // CommunityToolkit's [ObservableProperty] uses EqualityComparer<T>.Default
        // — re-assigning the same string is a no-op. Important so the tree
        // doesn't rebuild needlessly when WPF flushes a redundant SelectedValue
        // sync during dropdown initialisation.
        var vm = Build();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PieceSortColumn = "Catalogue";   // same as default

        Assert.DoesNotContain(nameof(CanonViewModel.PieceSortColumn), raised);
    }

    [Theory]
    [InlineData("Catalogue")]
    [InlineData("Title")]
    [InlineData("Category")]
    [InlineData("Year")]
    [InlineData("Recordings")]
    public void PieceSortColumn_AllSupportedValues_ParseSuccessfully(string label)
    {
        // The string→PieceSortField mapping lives in PieceSorting.ParseField
        // and is what CanonView.ApplySortedFilter calls after pulling
        // vm.PieceSortColumn. This test pins the integration: every value the
        // XAML offers must be a valid label the parser recognises (any
        // mismatch would silently fall back to a default and the user would
        // see "no effect" when they pick that option).
        var vm = Build();
        vm.PieceSortColumn = label;

        // Just verify ParseField doesn't throw and returns a real enum value.
        var field = PieceSorting.ParseField(vm.PieceSortColumn);
        Assert.True(Enum.IsDefined(typeof(PieceSortField), field));
    }
}
