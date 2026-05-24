namespace CDArchive.App.Services;

/// <summary>
/// Abstraction over WPF's <c>Microsoft.Win32.SaveFileDialog</c> /
/// <c>OpenFileDialog</c> so view-models can request file paths without
/// referencing the Win32 shell dialog APIs directly (H3). The production
/// implementation (<see cref="WpfFileDialogService"/>) shows real shell
/// dialogs; tests substitute a fake that returns scripted paths.
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Show a save-file dialog. Returns the chosen path, or null when the
    /// user cancels. <paramref name="filter"/> follows the Win32 filter
    /// convention (e.g. <c>"JSON files (*.json)|*.json|All files (*.*)|*.*"</c>).
    /// </summary>
    string? PickSaveFile(string title, string filter, string defaultExt,
                         string? defaultFileName = null, string? initialDirectory = null);

    /// <summary>
    /// Show an open-file dialog. Returns the chosen path, or null when the
    /// user cancels.
    /// </summary>
    string? PickOpenFile(string title, string filter, string defaultExt);
}
