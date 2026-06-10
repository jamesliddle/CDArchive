using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// Regression tests for <see cref="AlbumsViewModel.ResolveLiveAlbum"/>, the
/// helper that fixes the CanonView "edit album from Show Albums creates a
/// duplicate" bug. When the Canon view opens the album editor, the album it
/// hands over comes from a <c>PieceReferenceIndex</c> hit — and the index is
/// commonly built from a DIFFERENT album instance set than
/// <see cref="AlbumsViewModel.AllAlbums"/> (CanonViewModel does its own fresh
/// DB load for the index at startup, before the Albums view initialises). A
/// plain reference match then misses, the editor's clone is appended as a
/// second album, and the next index rebuild walks both — so a removed
/// piece-ref's badge never decrements (its hit survives on the stale
/// duplicate). ResolveLiveAlbum collapses the foreign instance back onto the
/// canonical AllAlbums one via IdentityKey.
/// </summary>
public class AlbumsViewModelResolveLiveAlbumTests
{
    private static AlbumsViewModel Build()
    {
        var svc      = Substitute.For<ICanonDataService>();
        var refIndex = new PieceReferenceIndex();
        var player   = new PlayerViewModel(
            Substitute.For<IAudioPlayerService>(),
            Substitute.For<IArchiveAudioLocator>(),
            Substitute.For<IArchiveSettings>(),
            Substitute.For<ICanonDataService>());
        var dialogs  = new Infrastructure.RecordingDialogService();
        return new AlbumsViewModel(svc, refIndex, player, dialogs);
    }

    [Fact]
    public void ResolveLiveAlbum_ReferenceMatch_ReturnsSameInstance()
    {
        var vm = Build();
        var live = new CanonAlbum { Title = "Jandó 10" };
        vm.AllAlbums.Add(live);

        var resolved = vm.ResolveLiveAlbum(live);

        Assert.Same(live, resolved);
    }

    [Fact]
    public void ResolveLiveAlbum_DifferentInstanceSameIdentityKey_ReturnsLiveInstance()
    {
        // The headline case: `foreign` mirrors a from-index instance that
        // shares the live album's identity (same Label|Catalogue|Title|Subtitle)
        // but is a distinct object. ResolveLiveAlbum must return the AllAlbums
        // instance so the caller edits + replaces it rather than appending.
        var vm = Build();
        var live = new CanonAlbum
        {
            Title           = "Beethoven Piano Sonatas Jandó 10",
            Label           = "Naxos",
            CatalogueNumber = "8.550045",
        };
        vm.AllAlbums.Add(live);

        var foreign = new CanonAlbum
        {
            Title           = "Beethoven Piano Sonatas Jandó 10",
            Label           = "Naxos",
            CatalogueNumber = "8.550045",
        };

        var resolved = vm.ResolveLiveAlbum(foreign);

        Assert.Same(live, resolved);
        Assert.NotSame(foreign, resolved);
    }

    [Fact]
    public void ResolveLiveAlbum_TitleOnlyIdentity_StillMatches()
    {
        // Most of the user's albums carry no Label/Catalogue — IdentityKey
        // folds in Title|Subtitle precisely so those still resolve.
        var vm = Build();
        var live = new CanonAlbum { Title = "Beethoven Piano Sonatas Jandó 10" };
        vm.AllAlbums.Add(live);

        var foreign = new CanonAlbum { Title = "Beethoven Piano Sonatas Jandó 10" };

        Assert.Same(live, vm.ResolveLiveAlbum(foreign));
    }

    [Fact]
    public void ResolveLiveAlbum_NoMatch_ReturnsCandidateUnchanged()
    {
        var vm = Build();
        vm.AllAlbums.Add(new CanonAlbum { Title = "Some Other Album" });

        var candidate = new CanonAlbum { Title = "Not In The List" };

        // No match → return the candidate as-is (a genuinely new album).
        Assert.Same(candidate, vm.ResolveLiveAlbum(candidate));
    }

    [Fact]
    public void ResolveLiveAlbum_NullIdentityKeyCandidate_DoesNotMatchOnEmptyKey()
    {
        // A candidate with every identity field blank has a null IdentityKey
        // and must NOT collapse onto some other blank-identity album.
        var vm = Build();
        var blankLive = new CanonAlbum();   // null IdentityKey
        vm.AllAlbums.Add(blankLive);

        var blankCandidate = new CanonAlbum();   // also null IdentityKey, different instance

        // Reference miss + null key → no IdentityKey match attempted → returns candidate.
        Assert.Same(blankCandidate, vm.ResolveLiveAlbum(blankCandidate));
    }
}
