using CDArchive.App.Tests.Infrastructure;
using CDArchive.App.ViewModels;
using CDArchive.Core.Models;
using CDArchive.Core.Services;
using NSubstitute;

namespace CDArchive.App.Tests.ViewModels;

/// <summary>
/// VM-level regression tests for <see cref="ImportExportViewModel"/>'s
/// dialog interactions: the Restore command must confirm before
/// overwriting SQLite data, and it must respect cancelling.
/// </summary>
public class ImportExportViewModelDialogTests
{
    private static (ImportExportViewModel vm, RecordingDialogService dialogs,
                    ScriptedFileDialogService fileDialogs, ICanonDataService svc) Build()
    {
        var svc         = Substitute.For<ICanonDataService>();
        var dialogs     = new RecordingDialogService();
        var fileDialogs = new ScriptedFileDialogService();
        var vm          = new ImportExportViewModel(svc, dialogs, fileDialogs);
        return (vm, dialogs, fileDialogs, svc);
    }

    [Fact]
    public async Task RestoreFromJson_WhenBothPicksCancelled_DoesNotPromptForConfirmation()
    {
        var (vm, dialogs, fileDialogs, svc) = Build();
        // Both PickOpenFile calls return null (queue empty → simulates cancel).

        await vm.RestoreFromJsonCommand.ExecuteAsync(null);

        Assert.Empty(dialogs.ConfirmCalls);
        Assert.Equal(2, fileDialogs.OpenCalls.Count);  // asked for composers + pieces
        Assert.Contains("nothing changed", vm.StatusMessage);
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
    }

    [Fact]
    public async Task RestoreFromJson_AsksForConfirmation_BeforeOverwritingSqlite()
    {
        var (vm, dialogs, fileDialogs, _) = Build();
        // Simulate the user picking a composers file; cancelling the pieces dialog.
        // We don't enqueue a real file path that exists, but the confirmation
        // happens BEFORE the file is actually read — so a non-existent path is
        // fine as long as the user cancels the confirmation.
        fileDialogs.OpenPathResponses.Enqueue("/tmp/composers.json");
        fileDialogs.OpenPathResponses.Enqueue(null);
        dialogs.ConfirmResponse = false;  // user cancels at the confirmation prompt

        await vm.RestoreFromJsonCommand.ExecuteAsync(null);

        var call = Assert.Single(dialogs.ConfirmCalls);
        Assert.Equal("Confirm Restore", call.Title);
        Assert.Contains("overwrite", call.Message);
        Assert.Contains("composers.json", call.Message);
    }

    [Fact]
    public async Task RestoreFromJson_WhenUserCancelsConfirmation_DoesNotTouchSqlite()
    {
        var (vm, dialogs, fileDialogs, svc) = Build();
        fileDialogs.OpenPathResponses.Enqueue("/tmp/composers.json");
        fileDialogs.OpenPathResponses.Enqueue(null);
        dialogs.ConfirmResponse = false;

        await vm.RestoreFromJsonCommand.ExecuteAsync(null);

        // The confirmation came back false; the VM must NOT have proceeded to
        // read the file or call any Save* on the data service.
        await svc.DidNotReceive().SaveComposersAsync(Arg.Any<List<CanonComposer>>());
        await svc.DidNotReceive().SavePiecesAsync(Arg.Any<List<CanonPiece>>());
    }
}
