using System.ComponentModel;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level tests for the composer sort + filter surface that H2 slice 2
/// migrated off <c>CanonView</c>'s code-behind:
/// <see cref="CanonViewModel.ComposerSortColumn"/> (new observable property
/// replacing the View's <c>_sortColumn</c> field) and
/// <see cref="CanonViewModel.ApplyComposerSort"/> (the sort helper moved from
/// the View).
///
/// <para>The "the View reacts via PropertyChanged" half is smoke-tested
/// manually (a headless run can't realise WPF containers); these tests pin
/// the VM contract that <c>CanonView.OnViewModelPropertyChanged</c> depends
/// on.</para>
/// </summary>
public class CanonViewModelComposerSortTests
{
    private static CanonViewModel Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>());
        var dialogs  = new RecordingDialogService();
        var albumsVm = new AlbumsViewModel(svc, refIndex, player, dialogs);
        var tracksVm = new TracksViewModel(albumsVm, svc, refIndex, player, dialogs);
        return new CanonViewModel(svc, refIndex, albumsVm, tracksVm, player, dialogs);
    }

    // ── ComposerSortColumn observable property ───────────────────────────────

    [Fact]
    public void ComposerSortColumn_Default_IsPieces()
    {
        // Matches the pre-fix XAML SelectedIndex="0" + ComboBoxItem order.
        // The default has to be "Pieces" so opening the Canon view for the
        // first time produces the same sort the user saw historically.
        var vm = Build();
        Assert.Equal("Pieces", vm.ComposerSortColumn);
    }

    [Fact]
    public void ComposerSortColumn_Set_RaisesPropertyChangedWithCorrectName()
    {
        // CanonView.OnViewModelPropertyChanged switches on
        // nameof(CanonViewModel.ComposerSortColumn) to trigger a tree rebuild —
        // pin the property-name contract here so a refactor that renames the
        // property without updating the View handler fails this test.
        var vm = Build();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ComposerSortColumn = "Name";

        Assert.Contains(nameof(CanonViewModel.ComposerSortColumn), raised);
    }

    [Fact]
    public void ComposerSortColumn_SetToSameValue_DoesNotRaisePropertyChanged()
    {
        // CommunityToolkit's [ObservableProperty] uses EqualityComparer<T>.Default
        // — re-assigning the same string is a no-op. Important so the tree
        // doesn't rebuild needlessly when WPF flushes a redundant SelectedValue
        // sync (which happens during dropdown initialisation).
        var vm = Build();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.ComposerSortColumn = "Pieces";   // same as default

        Assert.DoesNotContain(nameof(CanonViewModel.ComposerSortColumn), raised);
    }

    // ── ApplyComposerSort helper ─────────────────────────────────────────────

    [Fact]
    public void ApplyComposerSort_Name_OrdersBySortName_CaseInsensitive()
    {
        var vm = Build();
        vm.ComposerSortColumn = "Name";
        var beethoven = new CanonComposer { Name = "Beethoven, Ludwig van", SortName = "Beethoven" };
        var brahms    = new CanonComposer { Name = "brahms, Johannes",      SortName = "brahms"    };
        var alkan     = new CanonComposer { Name = "Alkan, Charles-Valentin", SortName = "Alkan"   };

        var sorted = vm.ApplyComposerSort(new[] { brahms, beethoven, alkan }).ToList();

        Assert.Equal(new[] { alkan, beethoven, brahms }, sorted);
    }

    [Fact]
    public void ApplyComposerSort_Born_OrdersByBirthYear_NullsLast()
    {
        var vm = Build();
        vm.ComposerSortColumn = "Born";
        var beethoven = new CanonComposer { Name = "Beethoven", SortName = "Beethoven", BirthDate = "1770-12-16" };
        var brahms    = new CanonComposer { Name = "Brahms",    SortName = "Brahms",    BirthDate = "1833-05-07" };
        var unknown   = new CanonComposer { Name = "Anonymous", SortName = "Anonymous", BirthDate = null };

        var sorted = vm.ApplyComposerSort(new[] { brahms, unknown, beethoven }).ToList();

        Assert.Equal(beethoven, sorted[0]);
        Assert.Equal(brahms,    sorted[1]);
        Assert.Equal(unknown,   sorted[2]);
    }

    [Fact]
    public void ApplyComposerSort_Pieces_DefaultDescending()
    {
        // "Pieces" sorts by piece count descending by default (most prolific
        // composer at the top). Verifies the ParseField label→(field, direction)
        // mapping is honoured by the VM helper too — that's the contract the
        // pre-fix code stamped, and ComposerSorting.ParseField is the single
        // source of truth.
        var vm = Build();
        vm.ComposerSortColumn = "Pieces";
        var a = new CanonComposer { Name = "A", SortName = "A" };
        var b = new CanonComposer { Name = "B", SortName = "B" };
        var c = new CanonComposer { Name = "C", SortName = "C" };
        vm.Pieces.Add(new CanonPiece { Composer = "A", Title = "p1" });
        vm.Pieces.Add(new CanonPiece { Composer = "A", Title = "p2" });
        vm.Pieces.Add(new CanonPiece { Composer = "A", Title = "p3" });
        vm.Pieces.Add(new CanonPiece { Composer = "B", Title = "p1" });

        var sorted = vm.ApplyComposerSort(new[] { c, a, b }).ToList();

        Assert.Equal(a, sorted[0]);  // 3 pieces
        Assert.Equal(b, sorted[1]);  // 1 piece
        Assert.Equal(c, sorted[2]);  // 0 pieces
    }

    [Fact]
    public void ApplyComposerSort_EmptySequence_ReturnsEmpty()
    {
        var vm = Build();
        Assert.Empty(vm.ApplyComposerSort(Array.Empty<CanonComposer>()));
    }
}
