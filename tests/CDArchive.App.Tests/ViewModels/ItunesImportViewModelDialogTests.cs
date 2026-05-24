using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for <see cref="ItunesImportViewModel"/>'s
/// dialog interaction (info on empty selection, error on import failure
/// — both routed through <see cref="IDialogService"/> after H3).
/// </summary>
public class ItunesImportViewModelDialogTests
{
    private static (ItunesImportViewModel vm, RecordingDialogService dialogs, ICanonDataService data) Build()
    {
        var data    = Substitute.For<ICanonDataService>();
        var itunes  = new ItunesLibraryReference();
        var dialogs = new RecordingDialogService();
        var vm      = new ItunesImportViewModel(data, itunes, dialogs);
        return (vm, dialogs, data);
    }

    [Fact]
    public async Task ImportTracksAsync_OnEmptySelection_ShowsInfo_AndDoesNotSave()
    {
        var (vm, dialogs, data) = Build();

        await vm.ImportTracksAsync(Array.Empty<ItunesTrack>());

        Assert.Single(dialogs.InfoCalls);
        Assert.Equal("Nothing to import", dialogs.InfoCalls[0].Title);
        Assert.Contains("No tracks selected", dialogs.InfoCalls[0].Message);
        await data.DidNotReceive().SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(),
            Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(),
            Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
    }

    [Fact]
    public async Task ImportTracksAsync_WhenLoadThrows_ShowsErrorDialog_AndStatusReflectsFailure()
    {
        var (vm, dialogs, data) = Build();
        // Make the very first DB hit (LoadComposersAsync) blow up so the
        // catch block runs without our test setup needing to construct a
        // valid import payload.
        data.LoadComposersAsync().Throws(new InvalidOperationException("DB unreachable"));

        var tracks = new[]
        {
            // Positional record ctor (TrackId, PersistentId, DiscNumber,
            // TrackNumber, Name, DurationMs, Genre, Composer, Album,
            // AlbumArtist, Artist, DateAdded, Location). Only TrackId +
            // Name + Composer + Album matter for the catch-path test.
            new ItunesTrack(1, null, 1, 1, "Track 1", 60000, null, "Test, A", "X", null, null, null, null),
        };
        await vm.ImportTracksAsync(tracks);

        Assert.Single(dialogs.ErrorCalls);
        Assert.Equal("Import error", dialogs.ErrorCalls[0].Title);
        Assert.Contains("DB unreachable", dialogs.ErrorCalls[0].Message);
        Assert.Contains("Import failed", vm.StatusMessage);
    }
}
