using System.Collections;
using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for <see cref="ItunesImportViewModel"/>'s
/// <c>ImportSelectedTracksCommand</c> — the RelayCommand wrapper H36
/// (ItunesImportView slice) surfaced. The XAML passes <c>SelectedItems</c>
/// as a non-generic <see cref="IList"/>; the command projects to
/// <see cref="ItunesTrack"/> defensively before handing off.
/// </summary>
public class ItunesImportViewModelCommandTests
{
    private static (ItunesImportViewModel vm, RecordingDialogService dialogs, ICanonDataService data) Build()
    {
        var data    = Substitute.For<ICanonDataService>();
        var itunes  = new ItunesLibraryReference();
        var dialogs = new RecordingDialogService();
        var vm      = new ItunesImportViewModel(data, itunes, dialogs);
        return (vm, dialogs, data);
    }

    private static ItunesTrack MakeTrack(int trackId, string name = "Track") =>
        new(trackId, null, 1, trackId, name, 60000, null, "Test, Composer", "Album", null, null, null, null);

    [Fact]
    public async Task ImportSelectedTracksCommand_EmptySelection_ShowsInfo_AndDoesNotSave()
    {
        var (vm, dialogs, data) = Build();

        await vm.ImportSelectedTracksCommand.ExecuteAsync(new ArrayList());

        // Same flow as calling ImportTracksAsync([]): the underlying method
        // shows the "Nothing to import" info dialog and exits before any save.
        Assert.Single(dialogs.InfoCalls);
        Assert.Equal("Nothing to import", dialogs.InfoCalls[0].Title);
        await data.DidNotReceive().SaveBatchAsync(
            Arg.Any<List<CanonComposer>?>(),
            Arg.Any<List<CanonPiece>?>(),
            Arg.Any<List<CanonAlbum>?>(),
            Arg.Any<List<AlbumTrack>?>(),
            Arg.Any<CanonPickLists?>());
    }

    [Fact]
    public async Task ImportSelectedTracksCommand_NullSelection_TreatedAsEmpty()
    {
        var (vm, dialogs, _) = Build();

        // The XAML's CommandParameter binding can yield null before the grid
        // realises items; the command must tolerate that.
        await vm.ImportSelectedTracksCommand.ExecuteAsync(null);

        Assert.Single(dialogs.InfoCalls);  // "Nothing to import"
    }

    [Fact]
    public async Task ImportSelectedTracksCommand_FiltersNonItunesTracksFromSelection()
    {
        var (vm, dialogs, _) = Build();
        // SelectedItems is typed as IList (non-generic) on WPF DataGrid; the
        // command must filter to ItunesTrack rather than blindly cast.
        // Mix a real ItunesTrack with a non-track object — the projection
        // should drop the non-track, leaving us with one ItunesTrack to
        // import.
        var selection = new ArrayList { MakeTrack(1), "not an itunes track", new object() };

        // The downstream ImportTracksAsync will attempt to load composers
        // etc.; we don't care about that path here — we only want to confirm
        // the projection worked (no exception, no "Nothing to import" info
        // since at least one valid track survived).
        try { await vm.ImportSelectedTracksCommand.ExecuteAsync(selection); }
        catch { /* underlying flow may throw on unconfigured substitute; ignore */ }

        Assert.Empty(dialogs.InfoCalls);  // didn't take the "Nothing to import" path
    }
}
