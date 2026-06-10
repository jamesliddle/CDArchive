using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Tests for promoting a composer's catalogue prefixes into the global
/// Catalogues pick list. When the user adds a per-composer prefix (e.g.
/// "Anh." on Beethoven), it should also land in
/// <see cref="CanonPickLists.CatalogPrefixes"/> so it's available app-wide
/// (the global Pick Lists screen, other composers' dropdowns), persisted in
/// the same transaction as the composer.
/// </summary>
public class CanonViewModelPrefixPromotionTests
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

    // ── Edit composer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task EditComposer_NewPrefix_AddedToGlobalPickList_AndSavedAtomically()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { CatalogPrefixes = { "Op.", "WoO" } };

        var composer = new CanonComposer
        {
            Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van",
            CatalogPrefixes = new List<string> { "Op." },
        };
        vm.Composers.Add(composer);
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        // Editor adds "Anh." to the composer's prefixes.
        composer.CatalogPrefixes!.Add("Anh.");

        await vm.CompleteEditComposerAsync(composer, snapshot);

        // Promoted into the global list…
        Assert.Contains("Anh.", vm.PickLists.CatalogPrefixes);
        // …and persisted in one transaction (composers + pick lists), not the
        // composers-only path.
        await svc.Received(1).SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(), Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(), Arg.Any<List<AlbumTrack>?>(),
            vm.PickLists);
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
    }

    [Fact]
    public async Task EditComposer_PrefixAlreadyGlobal_NoChange_ComposersOnlySave()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { CatalogPrefixes = { "Op.", "WoO" } };

        var composer = new CanonComposer
        {
            Name = "Beethoven, Ludwig van", SortName = "Beethoven, Ludwig van",
            CatalogPrefixes = new List<string> { "Op.", "WoO" },   // both already global
        };
        vm.Composers.Add(composer);
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        await vm.CompleteEditComposerAsync(composer, snapshot);

        Assert.Equal(new[] { "Op.", "WoO" }, vm.PickLists.CatalogPrefixes);
        await svc.Received(1).SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        await svc.DidNotReceive().SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(), Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(), Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
    }

    [Fact]
    public async Task EditComposer_PrefixDiffersOnlyByCase_NotDuplicatedInGlobal()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { CatalogPrefixes = { "Op." } };

        var composer = new CanonComposer
        {
            Name = "X", SortName = "X",
            CatalogPrefixes = new List<string> { "op." },   // case variant of existing
        };
        vm.Composers.Add(composer);
        var snapshot = CanonViewModel.CaptureComposerSnapshot(composer);

        await vm.CompleteEditComposerAsync(composer, snapshot);

        // No case-variant duplicate added.
        Assert.Single(vm.PickLists.CatalogPrefixes);
        Assert.Equal("Op.", vm.PickLists.CatalogPrefixes[0]);
    }

    // ── New composer ──────────────────────────────────────────────────────────

    [Fact]
    public async Task NewComposer_NewPrefix_AddedToGlobalPickList_AndSavedAtomically()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { CatalogPrefixes = { "Op." } };

        var composer = new CanonComposer
        {
            Name = "Newcomer", SortName = "Newcomer",
            CatalogPrefixes = new List<string> { "Hess" },
        };

        await vm.NewComposerCommand.ExecuteAsync(composer);

        Assert.Contains("Hess", vm.PickLists.CatalogPrefixes);
        await svc.Received(1).SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(), Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(), Arg.Any<List<AlbumTrack>?>(),
            vm.PickLists);
    }

    [Fact]
    public async Task NewComposer_SaveFails_RollsBackGlobalPrefixAddition()
    {
        var vm = Build(out var svc);
        vm.PickLists = new CanonPickLists { CatalogPrefixes = { "Op." } };
        svc.SaveBatchAsync(
                Arg.Any<List<CanonComposer>?>(), Arg.Any<List<CanonPiece>?>(),
                Arg.Any<List<CanonAlbum>?>(), Arg.Any<List<AlbumTrack>?>(),
                Arg.Any<CanonPickLists?>())
            .Returns<Task>(_ => throw new InvalidOperationException("disk full"));

        var composer = new CanonComposer
        {
            Name = "Newcomer", SortName = "Newcomer",
            CatalogPrefixes = new List<string> { "Hess" },
        };

        await vm.NewComposerCommand.ExecuteAsync(composer);

        // Both the composer and the speculative global-prefix addition rolled back.
        Assert.DoesNotContain(composer, vm.Composers);
        Assert.DoesNotContain("Hess", vm.PickLists.CatalogPrefixes);
        Assert.Equal(new[] { "Op." }, vm.PickLists.CatalogPrefixes);
    }
}
